using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Setup
{
    /// <summary>
    /// Setting a PC up for a shared database from the game, as the app's Setup Help does (its setup/installers.js,
    /// help.js, remote-access.js and elevated.js): what each kind of PC needs, PostgreSQL and Tailscale downloaded from
    /// their makers with a checksum check, this PC's addresses, Tailscale's sign-in, what was installed (the app's own
    /// note, installed-by-app.json, so "Remove everything" in either knows what it may remove), and running the steps
    /// that need administrator rights through Windows' permission prompt (Setup/elevated.ps1). No Dalamud here.
    /// </summary>
    public static class WindowsSetup
    {
        public sealed record Download(string Name, string Version, int SizeMb, string Url, string Sha256);

        /// <summary>The exact files, with their SHA-256 checksums (the app's DOWNLOADS). A download that doesn't match is refused.</summary>
        public static readonly Download Postgres = new("PostgreSQL 18", "18.6", 350,
            "https://get.enterprisedb.com/postgresql/postgresql-18.6-5-windows-x64.exe", "227E2BC08091B4C2B8338C7D61B11D057F895711480B006F90BFCFCD2410A58A");
        public static readonly Download TailscaleX64 = new("Tailscale", "1.104.1", 30,
            "https://pkgs.tailscale.com/stable/tailscale-setup-1.104.1-amd64.msi", "6DA8BCDB0D45A214CAE0E8A88C2987AD48DEC134BF04DFFB9C05F1354C02B0B4");
        public static readonly Download TailscaleArm64 = new("Tailscale", "1.104.1", 30,
            "https://pkgs.tailscale.com/stable/tailscale-setup-1.104.1-arm64.msi", "CB9688912EB48FAF2F752B084EE638D80545832634DA5F1885514041071C95D0");

        /// <summary>The download for this PC's processor (PostgreSQL has no Windows ARM build; x64 runs there).</summary>
        public static Download TailscaleForThisPc() => RuntimeInformationArm() ? TailscaleArm64 : TailscaleX64;

        private static bool RuntimeInformationArm() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.Arm64;

        // ---- What each kind of PC needs (the app's ROLES and setupPlan) ----

        public sealed record Role(string Key, string Label, string Detail, bool Database, bool OtherPcs);

        public static readonly Role[] Roles =
        {
            new("onepc", "Everything on this PC", "This PC plays and keeps the database. No other PC connects.", true, false),
            new("dbhost", "This PC keeps the database for my other PCs", "Your other PCs (playing or just viewing) connect to this one.", true, true),
            new("connect", "This PC uses a database on another PC", "The database PC is already set up. This one runs the app (and maybe the game).", false, true),
            new("plugin", "This PC only plays the game", "Only the plugin runs here, sending to a database on another PC.", false, true),
        };

        public sealed record Plan(string Role, bool InstallPostgres, bool InstallTailscale, bool AlreadyPostgres, bool AlreadyTailscale,
            bool Tailscale, bool CreateDatabase, bool OpenConnections, bool AskConnection);

        public static Plan? SetupPlan(string role, bool sameNetwork, bool postgresHere, bool tailscaleHere)
        {
            var r = Roles.FirstOrDefault(x => x.Key == role);
            if (r == null) return null;
            var needsTailscale = r.OtherPcs && !sameNetwork;
            return new Plan(role, r.Database && !postgresHere, needsTailscale && !tailscaleHere, r.Database && postgresHere, needsTailscale && tailscaleHere,
                needsTailscale, r.Database, role == "dbhost", role is "connect" or "plugin");
        }

        /// <summary>PostgreSQL's installer reads its answers from a file (so the password never shows on a command line).</summary>
        public static string PostgresOptions(string masterPassword, int port = 5432)
        {
            if (masterPassword.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("The master password cannot contain line breaks.");
            return string.Join("\r\n", "mode=unattended", "unattendedmodeui=none", $"superpassword={masterPassword}", $"serverport={port}",
                "disable-components=pgAdmin,stackbuilder") + "\r\n";
        }

        /// <summary>A password suggestion: 16 letters and digits (no look-alikes such as 0/O and 1/l).</summary>
        public static string SuggestPassword()
        {
            const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
            return new string(RandomNumberGenerator.GetBytes(16).Select(b => chars[b % chars.Length]).ToArray());
        }

        // ---- Downloads ----

        public static async Task<string> Sha256Of(string file)
        {
            await using var read = File.OpenRead(file);
            return Convert.ToHexString(await SHA256.HashDataAsync(read));
        }

        /// <summary>Downloads a file (kept from an earlier try if it's there and matches), checking its checksum. Throws
        /// with a plain message when it fails or doesn't match; nothing is left behind.</summary>
        public static async Task<string> DownloadOnce(Download file, string folder, Action<double?> progress)
        {
            Directory.CreateDirectory(folder);
            var dest = Path.Combine(folder, Path.GetFileName(new Uri(file.Url).AbsolutePath));
            if (File.Exists(dest) && string.Equals(await Sha256Of(dest), file.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                progress(1);
                return dest;
            }
            var partial = dest + ".partial";
            try
            {
                using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(60) };
                using var response = await http.GetAsync(file.Url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode) throw new IOException($"the download site answered {(int)response.StatusCode}");
                var total = response.Content.Headers.ContentLength;
                await using (var from = await response.Content.ReadAsStreamAsync())
                await using (var to = File.Create(partial))
                {
                    var buffer = new byte[1 << 16];
                    long got = 0;
                    int n;
                    while ((n = await from.ReadAsync(buffer)) > 0)
                    {
                        await to.WriteAsync(buffer.AsMemory(0, n));
                        got += n;
                        progress(total is long t && t > 0 ? Math.Min(1, (double)got / t) : null);
                    }
                }
                if (!string.Equals(await Sha256Of(partial), file.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("the downloaded file isn't the expected one (its checksum doesn't match), so it wasn't used");
                File.Move(partial, dest, true);
                return dest;
            }
            catch (Exception e)
            {
                try { File.Delete(partial); } catch { /* nothing left behind */ }
                throw new IOException($"Couldn't download {Path.GetFileName(new Uri(file.Url).AbsolutePath)}: {e.Message}");
            }
        }

        public static string DownloadFolder => Path.Combine(Path.GetTempPath(), "xiv-fleet-downloads");

        // ---- This PC's addresses (the app's setup/help.js) ----

        /// <summary>Tailscale gives every PC an address from 100.64.0.0 to 100.127.255.255.</summary>
        public static bool IsTailscaleAddress(string? ip)
        {
            var m = Regex.Match(ip ?? "", @"^100\.(\d+)\.\d+\.\d+$");
            return m.Success && int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) is >= 64 and <= 127;
        }

        public static bool IsHomeNetworkAddress(string? ip) =>
            Regex.IsMatch(ip ?? "", @"^(192\.168|10)\.\d+\.\d+(\.\d+)?$") || Regex.IsMatch(ip ?? "", @"^172\.(1[6-9]|2\d|3[01])\.\d+\.\d+$");

        private static readonly Regex VirtualAdapter = new("vEthernet|WSL|Hyper-V|VirtualBox|VMware|Docker", RegexOptions.IgnoreCase);

        /// <summary>One IPv4 address of an adapter: its name, address and network prefix.</summary>
        public sealed record Address(string Adapter, string Ip, int Prefix);

        public static List<Address> Addresses()
        {
            var list = new List<Address>();
            try
            {
                foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                    foreach (var a in nic.GetIPProperties().UnicastAddresses)
                        if (a.Address.AddressFamily == AddressFamily.InterNetwork)
                            list.Add(new Address($"{nic.Name} {nic.Description}", a.Address.ToString(), a.PrefixLength));
                }
            }
            catch
            {
                // None found: nothing to offer.
            }
            return list;
        }

        /// <summary>This PC's Tailscale and home network addresses.</summary>
        public static (List<string> Tailscale, List<string> Home) PcAddresses(IEnumerable<Address> addresses)
        {
            var tailscale = new List<string>();
            var home = new List<string>();
            foreach (var a in addresses)
            {
                if (IsTailscaleAddress(a.Ip) || a.Adapter.Contains("tailscale", StringComparison.OrdinalIgnoreCase)) tailscale.Add(a.Ip);
                else if (IsHomeNetworkAddress(a.Ip) && !VirtualAdapter.IsMatch(a.Adapter)) home.Add(a.Ip);
            }
            return (tailscale.Distinct().ToList(), home.Distinct().ToList());
        }

        /// <summary>192.168.1.20/24 → 192.168.1.0/24.</summary>
        public static string? NetworkOf(string ip, int bits)
        {
            var parts = ip.Split('.');
            if (parts.Length != 4 || bits < 0 || bits > 32) return null;
            uint n = 0;
            foreach (var p in parts) { if (!byte.TryParse(p, out var b)) return null; n = (n << 8) | b; }
            var mask = bits == 0 ? 0u : 0xffffffffu << (32 - bits);
            var net = n & mask;
            return $"{net >> 24}.{(net >> 16) & 255}.{(net >> 8) & 255}.{net & 255}/{bits}";
        }

        /// <summary>The address ranges "Let other PCs connect" lets in: Tailscale, plus each home network this PC is on.</summary>
        public static List<string> RangesToAllow(IEnumerable<Address> addresses) =>
            new[] { "100.64.0.0/10" }.Concat(addresses
                .Where(a => IsHomeNetworkAddress(a.Ip) && !IsTailscaleAddress(a.Ip) && !a.Adapter.Contains("tailscale", StringComparison.OrdinalIgnoreCase) && !VirtualAdapter.IsMatch(a.Adapter))
                .Select(a => NetworkOf(a.Ip, a.Prefix)).OfType<string>()).Distinct().ToList();

        /// <summary>Whether a saved host is this PC itself (the database is on this PC).</summary>
        public static bool IsThisPc(string? host, IEnumerable<Address> addresses)
        {
            var h = (host ?? "").Trim().ToLowerInvariant();
            if (h.Length == 0 || h is "localhost" or "127.0.0.1" or "::1" || h.StartsWith('/')) return true;
            if (h == Environment.MachineName.ToLowerInvariant()) return true;
            return addresses.Any(a => a.Ip == h);
        }

        // ---- Tailscale ----

        public static string TailscaleExe => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } pf ? pf : "C:\\Program Files", "Tailscale", "tailscale.exe");

        public static bool TailscaleHere => OperatingSystem.IsWindows() && File.Exists(TailscaleExe);

        /// <summary>From `tailscale status --json`: signed in, this PC's Tailscale address, its state.</summary>
        public static (bool SignedIn, string? Address, string State) TailscaleStatus(string json)
        {
            try
            {
                var s = JsonNode.Parse(json)!;
                var state = s["BackendState"]?.GetValue<string>() ?? "unknown";
                var address = (s["Self"]?["TailscaleIPs"] as JsonArray)?.Select(x => x?.GetValue<string>()).FirstOrDefault(ip => ip != null && Regex.IsMatch(ip, @"^\d+\.\d+\.\d+\.\d+$"));
                return (state == "Running", address, state);
            }
            catch
            {
                return (false, null, "unknown");
            }
        }

        /// <summary>Tailscale's sign-in address from `tailscale up` output.</summary>
        public static string? LoginUrl(string output) => Regex.Match(output, @"https://login\.tailscale\.com/\S+") is { Success: true } m ? m.Value : null;

        /// <summary>Starts Tailscale's sign-in and opens its page in the browser (unattended: keep connected while no one is
        /// signed in to Windows, for the database PC). Done once the page opens, Tailscale says it's signed in, or 20 s pass.</summary>
        public static async Task<(bool Ok, string? Error)> TailscaleSignIn(bool unattended, Action<string> open)
        {
            if (!TailscaleHere) return (false, "Tailscale isn't installed on this PC yet.");
            var info = new ProcessStartInfo(TailscaleExe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("up");
            if (unattended) info.ArgumentList.Add("--unattended");
            using var p = Process.Start(info)!;
            var output = new StringBuilder();
            var done = new TaskCompletionSource<(bool, string?)>();
            void Look(string? line)
            {
                if (line == null) return;
                lock (output) output.AppendLine(line);
                if (LoginUrl(line) is { } url) { open(url); done.TrySetResult((true, null)); }
            }
            p.OutputDataReceived += (_, e) => Look(e.Data);
            p.ErrorDataReceived += (_, e) => Look(e.Data);
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _ = p.WaitForExitAsync().ContinueWith(_ => done.TrySetResult(p.ExitCode == 0 ? (true, null) : (false, output.ToString().Trim() is { Length: > 0 } o ? o : $"Tailscale stopped with code {p.ExitCode}.")));
            _ = Task.Delay(20000).ContinueWith(_ => done.TrySetResult((true, null)));
            return await done.Task;
        }

        // ---- What's on this PC ----

        public sealed record PostgresService(string Service, string DataDir, string? InstallDir);

        /// <summary>PostgreSQL's Windows services from "Name|PathName" lines.</summary>
        public static List<PostgresService> ParseServices(string output)
        {
            var found = new List<PostgresService>();
            foreach (var line in output.Split('\n').Select(l => l.TrimEnd('\r')))
            {
                var bar = line.IndexOf('|');
                if (bar <= 0) continue;
                var name = line[..bar].Trim();
                var pathName = line[(bar + 1)..];
                var d = Regex.Match(pathName, "\\s-D\\s+(?:\"([^\"]+)\"|(\\S+))");
                if (name.Length == 0 || !d.Success) continue;
                var e = Regex.Match(pathName, "^\\s*(?:\"([^\"]+)\"|(\\S+))");
                var exe = e.Success ? (e.Groups[1].Success ? e.Groups[1].Value : e.Groups[2].Value) : "";
                string? install = null;
                if (Regex.IsMatch(exe, @"\\bin\\pg_ctl\.exe$", RegexOptions.IgnoreCase))
                {
                    var bin = exe[..exe.LastIndexOf('\\')];
                    install = bin[..bin.LastIndexOf('\\')];
                }
                found.Add(new PostgresService(name, (d.Groups[1].Success ? d.Groups[1].Value : d.Groups[2].Value).Trim(), install));
            }
            return found;
        }

        public static async Task<List<PostgresService>> PostgresServices()
        {
            if (!OperatingSystem.IsWindows()) return new List<PostgresService>();
            var r = await PgBackups.Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
                "Get-CimInstance Win32_Service -Filter \"Name LIKE 'postgresql%'\" | ForEach-Object { $_.Name + '|' + $_.PathName }" });
            return ParseServices(r.Stdout);
        }

        /// <summary>What this PC already has.</summary>
        public static async Task<(bool Postgres, bool Tailscale)> Detect() => ((await PostgresServices()).Count > 0, TailscaleHere);

        // ---- What the app (or the plugin) installed: installed-by-app.json, the app's own note ----

        public sealed record Installed(int? PostgresPort, bool Tailscale);

        public static string InstalledFile(string folder) => Path.Combine(folder, "installed-by-app.json");

        public static Installed ReadInstalled(string folder)
        {
            try
            {
                var o = JsonNode.Parse(File.ReadAllText(InstalledFile(folder)))!;
                var port = o["postgres"]?["port"] is JsonValue p && p.TryGetValue<double>(out var n) ? (int?)n : null;
                var tailscale = o["tailscale"] is JsonValue t && t.TryGetValue<bool>(out var b) && b;
                return new Installed(port, tailscale);
            }
            catch
            {
                return new Installed(null, false);
            }
        }

        private static void WriteInstalled(string folder, Installed i)
        {
            Directory.CreateDirectory(folder);
            var o = new JsonObject { ["postgres"] = i.PostgresPort is int port ? new JsonObject { ["port"] = port } : null, ["tailscale"] = i.Tailscale };
            File.WriteAllText(InstalledFile(folder), o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        /// <summary>Notes what was just installed, keeping earlier notes.</summary>
        public static void RecordInstalled(string folder, int? postgresPort, bool tailscale)
        {
            var now = ReadInstalled(folder);
            WriteInstalled(folder, new Installed(postgresPort ?? now.PostgresPort, tailscale || now.Tailscale));
        }

        /// <summary>After removing them: what's no longer there.</summary>
        public static void ForgetInstalled(string folder, bool postgres, bool tailscale)
        {
            var now = ReadInstalled(folder);
            WriteInstalled(folder, new Installed(postgres ? null : now.PostgresPort, !tailscale && now.Tailscale));
        }

        // ---- The steps that need administrator rights ----

        /// <summary>One step for Setup/elevated.ps1: installPostgres (installer, optionFile), installTailscale (msi),
        /// openConnections (port, ranges), undoOpenConnections (port), removePostgres (port), removeTailscale.</summary>
        public sealed record Step(string Kind, string? Installer = null, string? OptionFile = null, string? Msi = null, int? Port = null, List<string>? Ranges = null);

        public sealed record StepResult(string Kind, bool Ok, string Message, List<string> Changed);

        public sealed record Elevated(bool Ok, List<StepResult>? Results, string? Error);

        /// <summary>The administrator script, as shipped in the plugin.</summary>
        public static string Script()
        {
            using var s = typeof(WindowsSetup).Assembly.GetManifestResourceStream("Setup/elevated.ps1")!;
            using var r = new StreamReader(s);
            return r.ReadToEnd();
        }

        public static List<StepResult> ParseResults(JsonNode? node)
        {
            var list = new List<StepResult>();
            var array = node as JsonArray ?? (node is JsonObject o ? new JsonArray(o.DeepClone()) : null);
            foreach (var x in array ?? new JsonArray())
            {
                if (x is not JsonObject r) continue;
                var changed = (r["changed"] as JsonArray)?.Select(c => c?.ToString() ?? "").ToList() ?? (r["changed"] is JsonValue one ? new List<string> { one.ToString() } : new List<string>());
                list.Add(new StepResult(r["kind"]?.ToString() ?? "", r["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && b, r["message"]?.ToString() ?? "", changed));
            }
            return list;
        }

        /// <summary>Runs the steps with administrator rights (one Windows permission prompt), stopping at the first that
        /// fails unless keepGoing. progress gets the results so far as steps finish.</summary>
        public static async Task<Elevated> RunElevated(IReadOnlyList<Step> steps, bool keepGoing, Action<List<StepResult>> progress)
        {
            if (!OperatingSystem.IsWindows()) return new Elevated(false, null, "This can only be done on Windows.");
            var dir = Path.Combine(Path.GetTempPath(), $"xiv-fleet-{Guid.NewGuid():N}");
            Directory.CreateDirectory(dir);
            var script = Path.Combine(dir, "elevated.ps1");
            var request = Path.Combine(dir, "request.json");
            var resultFile = Path.Combine(dir, "result.json");
            var progressFile = Path.Combine(dir, "progress.json");
            try
            {
                File.WriteAllText(script, Script(), new UTF8Encoding(true));
                var stepNodes = new JsonArray();
                foreach (var s in steps)
                {
                    var o = new JsonObject { ["kind"] = s.Kind };
                    if (s.Installer != null) o["installer"] = s.Installer;
                    if (s.OptionFile != null) o["optionFile"] = s.OptionFile;
                    if (s.Msi != null) o["msi"] = s.Msi;
                    if (s.Port != null) o["port"] = s.Port;
                    if (s.Ranges != null) o["ranges"] = new JsonArray(s.Ranges.Select(r => (JsonNode)r!).ToArray());
                    stepNodes.Add(o);
                }
                File.WriteAllText(request, new JsonObject { ["steps"] = stepNodes, ["keepGoing"] = keepGoing, ["resultFile"] = resultFile, ["progressFile"] = progressFile }.ToJsonString());
                var info = new ProcessStartInfo("powershell.exe", $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{script}\" -RequestFile \"{request}\"")
                {
                    UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process? p;
                try
                {
                    p = Process.Start(info);
                }
                catch (Win32Exception e) when (e.NativeErrorCode == 1223)
                {
                    return new Elevated(false, null, "Windows' permission prompt was turned down, so nothing was changed.");
                }
                catch (Exception e)
                {
                    return new Elevated(false, null, $"Windows couldn't start the change: {e.Message}");
                }
                if (p == null) return new Elevated(false, null, "Windows couldn't start the change.");
                var last = "";
                while (!p.HasExited)
                {
                    await Task.Delay(1000);
                    try
                    {
                        var text = File.ReadAllText(progressFile);
                        if (text != last) { last = text; progress(ParseResults(JsonNode.Parse(text))); }
                    }
                    catch
                    {
                        // Not written yet.
                    }
                }
                if (!File.Exists(resultFile)) return new Elevated(false, null, "The setup finished without saying how it went. Try again.");
                var result = JsonNode.Parse(File.ReadAllText(resultFile))!;
                var results = ParseResults(result["results"]);
                var error = result["error"]?.ToString();
                return new Elevated(result["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var b) && b, error == null ? results : results.Count > 0 ? results : null, error);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { /* the temp folder */ }
            }
        }
    }
}
