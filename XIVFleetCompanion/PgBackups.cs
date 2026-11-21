using Npgsql;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>A database connection for PostgreSQL's own tools (the password goes to them through their environment).</summary>
    public sealed record PgLogin(string Host, int Port, string Database, string User, string Password);

    /// <summary>
    /// The shared database's backups, as the app makes them (backups/*.js): PostgreSQL's own tools (pg_dump's compressed
    /// custom format, restored with pg_restore), named fleet-YYYYMMDD-HHMMSS.dump in a folder this PC chooses, the
    /// newest kept, an optional second copy, and backup.log beside them. The settings are the app's own file
    /// (backups.json in %APPDATA%\XIV Fleet Companion), so on a PC with both, either one's backups and settings are the
    /// other's. No Dalamud here, so it is tested on its own (tests/FleetWriterTests runs it with a real pg_dump).
    /// </summary>
    public static class PgBackups
    {
        public const string TestDatabase = "fleet_restore_test";

        /// <summary>Tables whose row counts are compared when a backup is tested (the app's TEST_TABLES).</summary>
        public static readonly string[] TestTables =
        {
            "companion_character_current", "companion_metric_history", "companion_metric_latest", "companion_submarine_snapshot",
            "companion_retainer_lookup", "companion_inventory_snapshot",
        };

        private static readonly Regex BackupFile = new(@"^fleet-(\d{4})(\d{2})(\d{2})-(\d{2})(\d{2})(\d{2})\.dump$");

        // ---- Settings (backups.json, shared with the app) ----

        public sealed record Settings(bool Enabled, string Folder, string CopyFolder, int Keep, string Time, string PgBin)
        {
            public static readonly Settings Defaults = new(false, "", "", 14, "03:30", "");
        }

        /// <summary>The last backup's result, as the app records it (at: milliseconds since 1970).</summary>
        public sealed record LastResult(bool Ok, long At, string Message, string? File);

        public const int KeepLowest = 1;
        public const int KeepHighest = 365;
        private static readonly Regex TimeOfDay = new("^([01]\\d|2[0-3]):[0-5]\\d$");

        /// <summary>The settings and last result saved in <paramref name="file"/> (defaults for anything missing or wrong).</summary>
        public static (Settings Settings, LastResult? Last) Load(string file)
        {
            JsonObject? saved = null;
            try { saved = JsonNode.Parse(File.ReadAllText(file)) as JsonObject; } catch { /* none yet, or damaged: defaults */ }
            var d = Settings.Defaults;
            if (saved == null) return (d, null);
            string Str(string key, string fallback) => saved[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : fallback;
            var enabled = saved["enabled"] is JsonValue e && e.TryGetValue<bool>(out var b) ? b : d.Enabled;
            var keep = saved["keep"] is JsonValue k && k.TryGetValue<double>(out var n) && n == Math.Floor(n) && n >= KeepLowest && n <= KeepHighest ? (int)n : d.Keep;
            var time = Str("time", d.Time);
            var settings = new Settings(enabled, Str("folder", ""), Str("copyFolder", ""), keep, TimeOfDay.IsMatch(time) ? time : d.Time, Str("pgBin", ""));
            LastResult? last = null;
            if (saved["last"] is JsonObject l)
            {
                last = new LastResult(l["ok"] is JsonValue ok && ok.TryGetValue<bool>(out var okb) && okb,
                    l["at"] is JsonValue at && at.TryGetValue<double>(out var atn) ? (long)atn : 0,
                    l["message"] is JsonValue m && m.TryGetValue<string>(out var ms) ? ms : "",
                    l["file"] is JsonValue f && f.TryGetValue<string>(out var fs) ? fs : null);
            }
            return (settings, last);
        }

        /// <summary>Checks settings from the window, as the app does: the settings, or why not.</summary>
        public static (Settings? Settings, string? Error) Validate(Settings input)
        {
            var s = input with { Folder = input.Folder.Trim(), CopyFolder = input.CopyFolder.Trim(), PgBin = input.PgBin.Trim() };
            if (s.Enabled && s.Folder.Length == 0) return (null, "Choose a folder for the backups first.");
            foreach (var f in new[] { s.Folder, s.CopyFolder })
                if (f.Length > 0 && !Path.IsPathFullyQualified(f) && !IsWindowsFullPath(f))
                    return (null, $"\"{f}\" is not a full folder path (for example E:\\FleetBackups or \\\\LAPTOP\\FleetBackups).");
            static string Bare(string p) => p.TrimEnd('\\', '/').ToLowerInvariant();
            if (s.Folder.Length > 0 && s.CopyFolder.Length > 0 && Bare(s.Folder) == Bare(s.CopyFolder))
                return (null, "The second copy needs a different folder (ideally on another PC or drive).");
            if (s.Keep < KeepLowest || s.Keep > KeepHighest) return (null, $"Keep between {KeepLowest} and {KeepHighest} backups.");
            if (!TimeOfDay.IsMatch(s.Time)) return (null, "Choose a time such as 03:30.");
            return (s, null);
        }

        private static bool IsWindowsFullPath(string p) => Regex.IsMatch(p, @"^([A-Za-z]:[\\/]|\\\\[^\\]+\\)");

        /// <summary>Saves the settings, keeping the last result.</summary>
        public static void Save(string file, Settings settings) => Write(file, settings, Load(file).Last);

        /// <summary>Records the last backup's result, keeping the settings.</summary>
        public static void SaveLast(string file, LastResult last) => Write(file, Load(file).Settings, last);

        private static void Write(string file, Settings s, LastResult? last)
        {
            var o = new JsonObject
            {
                ["enabled"] = s.Enabled, ["folder"] = s.Folder, ["copyFolder"] = s.CopyFolder, ["keep"] = s.Keep, ["time"] = s.Time, ["pgBin"] = s.PgBin,
            };
            if (last != null) o["last"] = new JsonObject { ["ok"] = last.Ok, ["at"] = last.At, ["message"] = last.Message, ["file"] = last.File };
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        // ---- Backup files ----

        public sealed record Backup(string Name, string File, DateTime TakenAt, long? Size);

        public static string BackupName(DateTime at) => $"fleet-{at:yyyyMMdd-HHmmss}.dump";

        /// <summary>When a backup was taken, from its name (this PC's time), or null for any other file.</summary>
        public static DateTime? TakenAt(string name)
        {
            var m = BackupFile.Match(name);
            if (!m.Success) return null;
            var n = m.Groups.Cast<Group>().Skip(1).Select(g => int.Parse(g.Value, CultureInfo.InvariantCulture)).ToArray();
            try { return new DateTime(n[0], n[1], n[2], n[3], n[4], n[5]); } catch { return null; }
        }

        /// <summary>The backups in a folder, newest first (empty when there is no folder).</summary>
        public static List<Backup> List(string folder)
        {
            if (folder.Length == 0 || !Directory.Exists(folder)) return new List<Backup>();
            var list = new List<Backup>();
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (TakenAt(name) is not DateTime at) continue;
                long? size = null;
                try { size = new FileInfo(file).Length; } catch { /* listed without a size */ }
                list.Add(new Backup(name, file, at, size));
            }
            return list.OrderByDescending(b => b.Name, StringComparer.Ordinal).ToList();
        }

        /// <summary>The backups beyond the newest <paramref name="keep"/> (the ones to remove).</summary>
        public static List<Backup> ToRemove(IEnumerable<Backup> list, int keep) =>
            list.OrderByDescending(b => b.Name, StringComparer.Ordinal).Skip(keep).ToList();

        /// <summary>Whether the daily backup is due: none since the last time it was due (today's time if passed, else
        /// yesterday's). Made by the app's scheduled task or the plugin, it counts either way.</summary>
        public static bool Due(Settings s, IEnumerable<Backup> backups, DateTime now)
        {
            if (!s.Enabled || s.Folder.Length == 0) return false;
            var hm = s.Time.Split(':');
            var today = now.Date.AddHours(int.Parse(hm[0], CultureInfo.InvariantCulture)).AddMinutes(int.Parse(hm[1], CultureInfo.InvariantCulture));
            var lastDue = now >= today ? today : today.AddDays(-1);
            return !backups.Any(b => b.TakenAt >= lastDue);
        }

        // ---- Get the backup tools (the app's backups/get-tools.js) ----

        /// <summary>A download from PostgreSQL's maker, with its SHA-256 checksum (the app's DOWNLOADS.pgtools).</summary>
        public sealed record Download(int Major, string Version, int SizeMb, string Url, string Sha256);

        /// <summary>PostgreSQL's Windows zips, one for each version a database can run: the tools match the database's
        /// version, as on the PC that has it (a newer pg_restore sends settings an older server doesn't know).</summary>
        public static readonly Download[] ToolDownloads =
        {
            new(14, "14.24", 351, "https://get.enterprisedb.com/postgresql/postgresql-14.24-4-windows-x64-binaries.zip", "8D5B5AEF56848DBD0562E5E02D613EB5CBC7D97CAE10B290CBFDABF1B4969088"),
            new(15, "15.19", 354, "https://get.enterprisedb.com/postgresql/postgresql-15.19-5-windows-x64-binaries.zip", "49E0690194D9FE2BB4D7D6587FDDDAA85A3EAAAFE2FD5480E0916FCB25D0143E"),
            new(16, "16.15", 356, "https://get.enterprisedb.com/postgresql/postgresql-16.15-5-windows-x64-binaries.zip", "43BB45F173A6F08CF1D29A97A6D8DEB119E8E8093A24C00D2D1001A0CCAA8281"),
            new(17, "17.11", 364, "https://get.enterprisedb.com/postgresql/postgresql-17.11-5-windows-x64-binaries.zip", "80379B2C04D51C30225532E0AE04509899141E9957ED096FE749D7FD9DF8F82F"),
            new(18, "18.6", 367, "https://get.enterprisedb.com/postgresql/postgresql-18.6-5-windows-x64-binaries.zip", "E2246BA91D22345BC3D017586C09EDE52D9DF180B1EEB480F050445F1CAD84E2"),
        };

        /// <summary>What pg_dump and pg_restore need from the zip (the app's KEEP), and what must be there.</summary>
        public static readonly Regex KeepFromZip = new(@"^pgsql/(bin/(pg_dump\.exe|pg_restore\.exe|libpq\.dll|libintl-\d+\.dll|libiconv-\d+\.dll|libwinpthread-\d+\.dll|liblz4\.dll|libzstd\.dll|zlib1\.dll|libcrypto-[\w-]+\.dll|libssl-[\w-]+\.dll)|commandlinetools_3rd_party_licenses\.txt)$");
        private static readonly string[] Required = { "pg_dump.exe", "pg_restore.exe", "libpq.dll" };
        private const uint DllNotFound = 0xC0000135;

        /// <summary>Which download for a database whose server_version_num is <paramref name="versionNum"/>, or why none.</summary>
        public static (Download? File, string? Message) ChooseDownload(int versionNum)
        {
            var major = versionNum / 10000;
            var file = ToolDownloads.FirstOrDefault(d => d.Major == major);
            if (file != null) return (file, null);
            return (null, $"The database runs PostgreSQL {(major > 0 ? major.ToString(CultureInfo.InvariantCulture) : "of an unknown version")}. " +
                          $"The tools can be fetched for PostgreSQL {ToolDownloads.Min(d => d.Major)} to {ToolDownloads.Max(d => d.Major)} only.");
        }

        /// <summary>Keeps only what the tools need from a downloaded zip (nothing else is unpacked).</summary>
        public static void ExtractTools(string zip, string dest)
        {
            Directory.CreateDirectory(dest);
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            foreach (var e in archive.Entries)
                if (KeepFromZip.IsMatch(e.FullName)) System.IO.Compression.ZipFileExtensions.ExtractToFile(e, Path.Combine(dest, e.Name), true);
        }

        /// <summary>Get the backup tools: downloads PostgreSQL's zip (its checksum checked), keeps only pg_dump, pg_restore
        /// and what they need in <paramref name="dir"/> (the app's own tools folder, so either finds them), checks they
        /// run, and deletes the download. Nothing is installed. The folder, or why not.</summary>
        public static async Task<(string? Bin, string? Version, string? Message)> GetTools(Download file, string dir, string tempDir, Action<string> progress)
        {
            var zip = Path.Combine(tempDir, Path.GetFileName(new Uri(file.Url).AbsolutePath));
            var fresh = dir + ".new";
            try
            {
                Directory.CreateDirectory(tempDir);
                progress("Downloading...");
                using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) })
                using (var response = await http.GetAsync(file.Url, System.Net.Http.HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var total = response.Content.Headers.ContentLength;
                    await using var from = await response.Content.ReadAsStreamAsync();
                    await using var to = File.Create(zip);
                    var buffer = new byte[1 << 16];
                    long done = 0;
                    var shown = -1;
                    int n;
                    while ((n = await from.ReadAsync(buffer)) > 0)
                    {
                        await to.WriteAsync(buffer.AsMemory(0, n));
                        done += n;
                        if (total is long t && t > 0 && (int)(done * 100 / t) != shown) progress($"Downloading... {shown = (int)(done * 100 / t)}%");
                    }
                }
                progress("Checking the download...");
                string sum;
                await using (var read = File.OpenRead(zip)) sum = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(read));
                if (!string.Equals(sum, file.Sha256, StringComparison.OrdinalIgnoreCase))
                    return (null, null, "The download isn't the file PostgreSQL's maker published (its checksum doesn't match), so it wasn't used. Try again later.");
                progress("Unpacking...");
                if (Directory.Exists(fresh)) Directory.Delete(fresh, true);
                ExtractTools(zip, fresh);
                var missing = Required.Where(f => !File.Exists(Path.Combine(fresh, f))).ToList();
                if (missing.Count > 0) return (null, null, $"The download couldn't be unpacked ({string.Join(", ", missing)} not in it).");
                progress("Checking they run...");
                var v = await Run(Path.Combine(fresh, "pg_dump.exe"), new[] { "--version" });
                if (!v.Ok)
                {
                    return (null, null, unchecked((uint)v.Code) == DllNotFound
                        ? "The tools need Microsoft's Visual C++ runtime, which isn't on this PC. Install \"Microsoft Visual C++ Redistributable\" (x64) from Microsoft's website, then try again."
                        : $"The tools didn't run: {(v.Stderr.Length > 0 ? v.Stderr : v.Stdout).Trim()}");
                }
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                Directory.Move(fresh, dir);
                return (dir, v.Stdout.Trim(), null);
            }
            catch (Exception e)
            {
                return (null, null, e.Message);
            }
            finally
            {
                try { File.Delete(zip); } catch { /* the temp folder is emptied by Windows */ }
                try { if (Directory.Exists(fresh)) Directory.Delete(fresh, true); } catch { /* next time */ }
            }
        }

        // ---- PostgreSQL's tools ----

        public sealed record Tools(string Bin, string Dump, string Restore, string Version, bool FromApp);

        public sealed record RunResult(bool Ok, int Code, string Stdout, string Stderr);

        public const string PasswordHint = " PostgreSQL's backup tools can't send a password with letters outside English on this PC: change the database's password to one with only English letters, numbers and symbols.";

        public static bool IsAscii(string text) => text.All(c => c < 128);

        /// <summary>A temporary folder whose path PostgreSQL's tools can open: the user's own, unless its path has letters
        /// outside English (a user name such as José), then one under ProgramData. As the app's toolTempDir.</summary>
        public static string ToolTempDir(string? temp = null, string? programData = null)
        {
            temp ??= Path.GetTempPath();
            return IsAscii(temp) ? temp
                : Path.Combine(programData ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "XIV Fleet Companion", "temp");
        }

        public static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

        public static async Task<RunResult> Run(string file, IEnumerable<string> args, PgLogin? login = null)
        {
            var info = new ProcessStartInfo(file)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            };
            foreach (var a in args) info.ArgumentList.Add(a);
            if (login != null)
            {
                info.Environment["PGPASSWORD"] = login.Password;
                info.Environment["PGCONNECT_TIMEOUT"] = "15";
            }
            try
            {
                using var p = Process.Start(info)!;
                var stdout = p.StandardOutput.ReadToEndAsync();
                var stderr = p.StandardError.ReadToEndAsync();
                await p.WaitForExitAsync();
                var err = await stderr;
                // The tools take the password through Windows' older text encoding (as the app's backups/tools.js says).
                if (p.ExitCode != 0 && login != null && !IsAscii(login.Password) && err.Contains("authentication", StringComparison.OrdinalIgnoreCase))
                    err += PasswordHint;
                return new RunResult(p.ExitCode == 0, p.ExitCode, await stdout, err);
            }
            catch (Exception e)
            {
                return new RunResult(false, -1, "", e.Message);
            }
        }

        /// <summary>C:\Program Files\PostgreSQL\&lt;version&gt;\bin, newest version first.</summary>
        public static List<string> ProgramFilesFolders(string? root = null)
        {
            root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) is { Length: > 0 } pf ? pf : "C:\\Program Files", "PostgreSQL");
            try
            {
                return Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>()
                    .Where(v => Regex.IsMatch(v, @"^\d+(\.\d+)?$"))
                    .OrderByDescending(v => double.Parse(v, CultureInfo.InvariantCulture)).Select(v => Path.Combine(root, v, "bin")).ToList();
            }
            catch
            {
                return new List<string>();
            }
        }

        /// <summary>"C:\Program Files\PostgreSQL\16\bin\pg_ctl.exe" runservice ... → its folder (PostgreSQL services).</summary>
        public static List<string> ServicePathsToFolders(string output)
        {
            var folders = new List<string>();
            foreach (var line in output.Split('\n'))
            {
                var m = Regex.Match(line, "^\\s*\"?([^\"]+?\\.exe)\"?", RegexOptions.IgnoreCase);
                if (!m.Success) continue;
                var exe = m.Groups[1].Value;
                var cut = exe.LastIndexOfAny(new[] { '\\', '/' });
                if (cut > 0) folders.Add(exe[..cut]);
            }
            return folders;
        }

        private static async Task<List<string>> ServiceFolders()
        {
            if (!OperatingSystem.IsWindows()) return new List<string>();
            var r = await Run("powershell.exe", new[] { "-NoProfile", "-NonInteractive", "-Command",
                "Get-CimInstance Win32_Service -Filter \"Name LIKE 'postgresql%'\" | ForEach-Object { $_.PathName }" });
            return ServicePathsToFolders(r.Stdout);
        }

        /// <summary>Finds pg_dump and pg_restore: the folder chosen, else a PostgreSQL service's, else Program Files, else
        /// the app's (or this plugin's) downloaded copy, else the PATH. The tools, or why not.</summary>
        /// <summary>The tools to use. serverMajor (the database's version, when known): pg_dump refuses a newer server, so
        /// the first tools at least that version are taken (an older PostgreSQL installed here for something else is passed
        /// over for the downloaded ones), and when none are, it says so. The same as the app's backups/tools.js.</summary>
        public static async Task<(Tools? Tools, string? Error)> FindTools(string chosen, string appToolsDir, int? serverMajor = null)
        {
            var candidates = chosen.Length > 0 ? new List<string> { chosen } : (await ServiceFolders()).Concat(ProgramFilesFolders()).Append(appToolsDir).ToList();
            var folders = candidates.Where(f => f.Length > 0 && File.Exists(Path.Combine(f, Exe("pg_dump"))) && File.Exists(Path.Combine(f, Exe("pg_restore")))).ToList();
            if (chosen.Length > 0 && folders.Count == 0) return (null, $"pg_dump was not found in {chosen}. Choose PostgreSQL's \"bin\" folder.");
            var tried = new List<int>();
            // The folders in order, then the PATH ("").
            foreach (var bin in chosen.Length > 0 ? folders : folders.Append(""))
            {
                var dump = bin.Length > 0 ? Path.Combine(bin, Exe("pg_dump")) : Exe("pg_dump");
                var restore = bin.Length > 0 ? Path.Combine(bin, Exe("pg_restore")) : Exe("pg_restore");
                var v = await Run(dump, new[] { "--version" });
                if (!v.Ok) continue;
                var major = ToolsMajor(v.Stdout);
                if (serverMajor == null || major == null || major >= serverMajor)
                    return (new Tools(bin, dump, restore, v.Stdout.Trim(), bin.Length > 0 && bin == appToolsDir), null);
                tried.Add(major.Value);
            }
            if (tried.Count > 0)
                return (null, $"The backup tools on this PC are PostgreSQL {tried.Max()}, older than the database's PostgreSQL {serverMajor}, which they can't back up. " +
                              $"Get the right ones with Get the backup tools{(chosen.Length > 0 ? ", or choose a newer PostgreSQL's \"bin\" folder" : "")}.");
            return (null, "PostgreSQL's backup tools (pg_dump) are not on this PC. Get them with Get the backup tools, or make the backups on the PC that has the database.");
        }

        /// <summary>The major version in pg_dump --version's answer ("pg_dump (PostgreSQL) 16.4" is 16), or null.</summary>
        public static int? ToolsMajor(string text)
        {
            var m = System.Text.RegularExpressions.Regex.Match(text, @"\(PostgreSQL\)\s+(\d+)");
            if (!m.Success) m = System.Text.RegularExpressions.Regex.Match(text, @"(\d+)(\.\d+)+");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
        }

        /// <summary>The database's PostgreSQL major version (16 for 16.4), or null when it can't be asked.</summary>
        public static async Task<int?> ServerMajor(PgLogin login)
        {
            try
            {
                await using var conn = new NpgsqlConnection(ConnectionString(login));
                await conn.OpenAsync();
                await using var cmd = new NpgsqlCommand("SHOW server_version_num", conn);
                return int.Parse((string)(await cmd.ExecuteScalarAsync())!, CultureInfo.InvariantCulture) / 10000;
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException or System.Net.Sockets.SocketException or TimeoutException or FormatException)
            {
                return null;
            }
        }

        private static string[] ConnectionArgs(PgLogin l) => new[] { "-h", l.Host, "-p", l.Port.ToString(CultureInfo.InvariantCulture), "-U", l.User };

        private static string ToolError(string what, RunResult r)
        {
            var text = (r.Stderr.Length > 0 ? r.Stderr : r.Stdout).Trim();
            var tail = string.Join(" ", text.Split('\n').Select(l => l.TrimEnd('\r')).TakeLast(3));
            return $"{what} failed: {(tail.Length > 0 ? tail : $"exit code {r.Code}")}";
        }

        /// <summary>Adds lines to backup.log in the backup folder (its last 1000 lines kept).</summary>
        public static void WriteLog(string folder, IEnumerable<string> lines)
        {
            try
            {
                var file = Path.Combine(folder, "backup.log");
                var old = File.Exists(file) ? File.ReadAllLines(file).Where(l => l.Length > 0).ToList() : new List<string>();
                var stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                var all = old.Concat(lines.Select(l => $"{stamp}  {l}")).TakeLast(1000);
                File.WriteAllText(file, string.Join("\r\n", all) + "\r\n");
            }
            catch
            {
                // The log is a help, never a reason to fail.
            }
        }

        private static void RemoveOld(string folder, int keep, List<string> log, string label)
        {
            foreach (var old in ToRemove(List(folder), keep))
            {
                try
                {
                    File.Delete(old.File);
                    log.Add($"Removed old {label} {old.Name}");
                }
                catch (Exception e)
                {
                    log.Add($"Could not remove old {label} {old.Name}: {e.Message}");
                }
            }
        }

        // Copies under a temporary name and renames when complete, so a half-copied file never looks like a backup.
        private static string CopyComplete(string file, string folder)
        {
            Directory.CreateDirectory(folder);
            var final = Path.Combine(folder, Path.GetFileName(file));
            var partial = final + ".partial";
            try
            {
                File.Copy(file, partial, true);
                if (new FileInfo(partial).Length != new FileInfo(file).Length) throw new IOException("the copy is not the same size as the backup");
                File.Move(partial, final, true);
            }
            catch
            {
                try { File.Delete(partial); } catch { /* nothing left behind */ }
                throw;
            }
            return final;
        }

        public sealed record BackupResult(bool Ok, DateTime At, string? File, long? Size, string? Copied, string? CopyError, string Message);

        private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture);

        /// <summary>One backup: pg_dump to a temporary name, read back with pg_restore --list (a complete archive), its
        /// final name, backups beyond Keep removed, then the second copy if one is set. Ok when the backup is made, even if
        /// the copy failed (CopyError says why).</summary>
        public static async Task<BackupResult> BackupNow(Tools tools, PgLogin login, Settings s, DateTime now)
        {
            var log = new List<string>();
            var folder = s.Folder;
            string? partial = null;
            string final;
            long size;
            try
            {
                if (folder.Length == 0) throw new IOException("No backup folder is chosen.");
                var root = Path.GetPathRoot(Path.GetFullPath(folder));
                if (!string.IsNullOrEmpty(root) && !Directory.Exists(root)) throw new IOException($"Drive {root} is not available (is it plugged in?).");
                Directory.CreateDirectory(folder);
                // Two backups in the same second (a restore's backup first, then the restore) must not share a name.
                var taken = now;
                while (File.Exists(Path.Combine(folder, BackupName(taken)))) taken = taken.AddSeconds(1);
                final = Path.Combine(folder, BackupName(taken));
                partial = final + ".partial";
                log.Add($"Starting backup of '{login.Database}' on {login.Host}:{login.Port}");
                var dump = await Run(tools.Dump, ConnectionArgs(login).Concat(new[] { "-d", login.Database, "-Fc", "-f", partial }), login);
                if (!dump.Ok) throw new IOException(ToolError("pg_dump", dump));
                var list = await Run(tools.Restore, new[] { "--list", partial });
                if (!list.Ok) throw new IOException(ToolError("Reading the new backup back", list));
                File.Move(partial, final);
                partial = null;
                size = new FileInfo(final).Length;
                log.Add($"Backup written: {final} ({Mb(size)} MB)");
                RemoveOld(folder, s.Keep, log, "backup");
            }
            catch (Exception e)
            {
                if (partial != null) { try { File.Delete(partial); } catch { /* nothing left behind */ } }
                log.Add($"FAILED: {e.Message}");
                if (folder.Length > 0 && Directory.Exists(folder)) WriteLog(folder, log);
                return new BackupResult(false, now, null, null, null, null, e.Message);
            }

            var message = $"Backed up ({Mb(size)} MB).";
            string? copied = null, copyError = null;
            if (s.CopyFolder.Length > 0)
            {
                try
                {
                    copied = CopyComplete(final, s.CopyFolder);
                    log.Add($"Copied to {copied}");
                    RemoveOld(s.CopyFolder, s.Keep, log, "copy");
                    message += " Second copy made.";
                }
                catch (Exception e)
                {
                    copyError = e.Message;
                    log.Add($"COPY FAILED (the backup in {folder} is fine): {e.Message}");
                    message += $" The second copy failed: {e.Message}";
                }
            }
            WriteLog(folder, log);
            return new BackupResult(true, now, final, size, copied, copyError, message);
        }

        public sealed record TableCount(string Table, long? Live, long? Restored);

        private static async Task<long?> CountRows(NpgsqlConnection c, string table)
        {
            await using (var exists = new NpgsqlCommand("SELECT to_regclass(@t) IS NOT NULL", c))
            {
                exists.Parameters.AddWithValue("t", table);
                if (!(bool)(await exists.ExecuteScalarAsync())!) return null;
            }
            await using var count = new NpgsqlCommand($"SELECT count(*) FROM {table}", c);
            return Convert.ToInt64(await count.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        private static string ConnectionString(PgLogin l, string? database = null) => new NpgsqlConnectionStringBuilder
        {
            Host = l.Host, Port = l.Port, Database = database ?? l.Database, Username = l.User, Password = l.Password, Timeout = 15, Pooling = false,
        }.ConnectionString;

        /// <summary>Loads a backup into a temporary database (fleet_restore_test), compares row counts with the live one
        /// and removes the temporary one. The live database is only read. Needs a login that may create databases.</summary>
        public static async Task<(bool Ok, List<TableCount> Rows, string Message)> Test(Tools tools, PgLogin login, string file)
        {
            await using var admin = new NpgsqlConnection(ConnectionString(login));
            await admin.OpenAsync();
            try
            {
                try
                {
                    await using (var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {TestDatabase}", admin)) await drop.ExecuteNonQueryAsync();
                    await using (var make = new NpgsqlCommand($"CREATE DATABASE {TestDatabase}", admin)) await make.ExecuteNonQueryAsync();
                }
                catch (PostgresException e) when (e.SqlState == "42501")
                {
                    return (false, new List<TableCount>(), $"The database login \"{login.User}\" may not create the temporary database a test needs. The backup was already checked as complete when it was made.");
                }
                var r = await Run(tools.Restore, ConnectionArgs(login).Concat(new[] { "-d", TestDatabase, "--no-owner", "--no-privileges", file }), login);
                if (!r.Ok) return (false, new List<TableCount>(), ToolError("Loading the backup", r));
                var rows = new List<TableCount>();
                await using (var restored = new NpgsqlConnection(ConnectionString(login, TestDatabase)))
                {
                    await restored.OpenAsync();
                    foreach (var t in TestTables) rows.Add(new TableCount(t, await CountRows(admin, t), await CountRows(restored, t)));
                }
                // The live database keeps growing after the backup, so more rows live than restored is expected.
                var bad = rows.Where(x => x.Live != null && (x.Restored == null || x.Restored > x.Live)).ToList();
                return bad.Count == 0
                    ? (true, rows, "The backup restores and its tables are all there.")
                    : (false, rows, $"The backup is missing or has odd counts for {string.Join(", ", bad.Select(b => b.Table))}.");
            }
            finally
            {
                NpgsqlConnection.ClearAllPools();
                try
                {
                    await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {TestDatabase}", admin);
                    await drop.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Left for the next test to remove.
                }
            }
        }

        /// <summary>Whether a file (any name, anywhere) is a backup of this database that pg_restore can read: null when
        /// it is, else why not.</summary>
        public static async Task<string?> CheckFile(Tools tools, string file)
        {
            if (file.Length == 0 || !File.Exists(file)) return $"{(file.Length > 0 ? file : "That file")} is not there.";
            var r = await Run(tools.Restore, new[] { "--list", file });
            if (!r.Ok || !r.Stdout.Contains("companion_character_current", StringComparison.Ordinal))
                return $"{Path.GetFileName(file)} isn't a backup of the fleet's database, so it can't be restored.";
            return null;
        }

        /// <summary>Run just before a restore, in one transaction: drops every fleet view and their function, and forgets
        /// which views.sql made them. pg_restore --clean drops only what is in the backup, and without CASCADE, so a view
        /// added by a later version would stop the restore. The views are made again by the next database check
        /// (DatabaseKeeper.CheckSoon), whether the restore worked or not. The same as the app's backups/run.js.</summary>
        public const string SetViewsAsideSql = @"DO $$
DECLARE v record;
BEGIN
  FOR v IN SELECT n.nspname AS schema, c.relname AS name FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
           WHERE c.relkind = 'v' AND n.nspname = current_schema() AND c.relname LIKE 'fleet\_%\_view' LOOP
    EXECUTE format('DROP VIEW IF EXISTS %I.%I CASCADE', v.schema, v.name);  -- by name: one dropped already, with a view it's built on, is skipped
  END LOOP;
  DROP FUNCTION IF EXISTS fleet_part_letter(int) CASCADE;
  IF to_regclass('companion_views') IS NOT NULL THEN DELETE FROM companion_views; END IF;
END $$;";

        /// <summary>A pause of the plugins on every PC (PausePlugins): disposing it takes the note down. Problem: why it
        /// couldn't pause them (nothing was paused).</summary>
        public sealed class Paused : IAsyncDisposable
        {
            internal NpgsqlConnection? Conn;
            internal System.Threading.CancellationTokenSource? Beat;
            public string? Problem { get; internal set; }

            public async ValueTask DisposeAsync()
            {
                Beat?.Cancel();
                if (Conn == null) return;
                try
                {
                    await using var cmd = new NpgsqlCommand("DELETE FROM companion_database_move WHERE id = 1 AND state = 'pausing'", Conn);
                    await cmd.ExecuteNonQueryAsync();
                }
                catch (NpgsqlException) { /* the note goes stale in 5 minutes anyway */ }
                await Conn.DisposeAsync();
                Conn = null;
            }
        }

        /// <summary>Pauses the plugins on every PC while the database is restored, as a move does (the app's
        /// backups/run.js pausePlugins): the move note says "pausing" (refreshed every 30 seconds; plugins wait while it's
        /// fresh), then this waits until no plugin has been busy for 15 seconds, up to 3 minutes. A database without the
        /// note's table has nothing to pause; a note saying the database moved away is left alone.</summary>
        public static async Task<Paused> PausePlugins(PgLogin login, int tries = 60, int everyMs = 3000)
        {
            var conn = new NpgsqlConnection(ConnectionString(login));
            await conn.OpenAsync();
            async Task<object?> One(string sql, params object[] args)
            {
                await using var cmd = new NpgsqlCommand(sql, conn);
                foreach (var a in args) cmd.Parameters.Add(new NpgsqlParameter { Value = a });
                return await cmd.ExecuteScalarAsync();
            }
            if (await One("SELECT to_regclass('companion_database_move') IS NOT NULL") is not true) { await conn.DisposeAsync(); return new Paused(); }
            await using (var set = new NpgsqlCommand(@"INSERT INTO companion_database_move (id, state, host, port, from_host, started_at, updated_at, moved_at)
                VALUES (1, 'pausing', $1, $2, 'restore', now(), now(), NULL)
                ON CONFLICT (id) DO UPDATE SET state = 'pausing', from_host = 'restore', started_at = now(), updated_at = now(), moved_at = NULL
                WHERE companion_database_move.state <> 'moved'", conn))
            {
                set.Parameters.Add(new NpgsqlParameter { Value = login.Host });
                set.Parameters.Add(new NpgsqlParameter { Value = login.Port });
                if (await set.ExecuteNonQueryAsync() == 0) { await conn.DisposeAsync(); return new Paused(); }
            }
            var paused = new Paused { Conn = conn, Beat = new System.Threading.CancellationTokenSource() };
            var token = paused.Beat.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await using var hb = new NpgsqlConnection(ConnectionString(login));
                    await hb.OpenAsync(token);
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(30000, token);
                        await using var cmd = new NpgsqlCommand("UPDATE companion_database_move SET updated_at = now() WHERE id = 1 AND state = 'pausing'", hb);
                        await cmd.ExecuteNonQueryAsync(token);
                    }
                }
                catch (Exception) { /* stopped, or the connection went: the note goes stale */ }
            });
            var quiet = 0;
            for (var i = 0; i < tries && quiet < 2; i++)
            {
                var busy = Convert.ToInt64(await One(@"SELECT count(*) FROM pg_stat_activity
                    WHERE datname = current_database() AND application_name = $1 AND pid <> pg_backend_pid()
                      AND (state <> 'idle' OR state_change > now() - interval '15 seconds')", DatabaseMove.ApplicationName), CultureInfo.InvariantCulture);
                quiet = busy == 0 ? quiet + 1 : 0;
                if (quiet < 2) await Task.Delay(everyMs);
            }
            if (quiet < 2)
            {
                await paused.DisposeAsync();
                return new Paused { Problem = "A plugin on another PC was still syncing after 3 minutes, so nothing was restored. Try again in a few minutes." };
            }
            return paused;
        }

        /// <summary>Puts the database back as it was in a backup: first a backup of how it is now (so this can be undone),
        /// then pg_restore --clean in one transaction, so a failure part way changes nothing. The fleet views are set
        /// aside first (SetViewsAsideSql); call DatabaseKeeper.CheckSoon afterwards, whatever the result.</summary>
        public static async Task<(bool Ok, BackupResult? Safety, string Message)> Restore(Tools tools, PgLogin login, Settings s, string file, DateTime now)
        {
            if (!File.Exists(file)) return (false, null, $"{file} is not there any more.");
            // Removes no old backups: the one being restored may be the oldest. The next daily backup tidies up.
            var safety = await BackupNow(tools, login, s with { CopyFolder = "", Keep = int.MaxValue }, now);
            if (!safety.Ok) return (false, safety, $"Nothing was restored: first backing up the database as it is now failed. {safety.Message}");
            Paused paused;
            try
            {
                paused = await PausePlugins(login);
            }
            catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
            {
                return (false, safety, $"Nothing was restored: the other PCs couldn't be paused first. {e.Message}");
            }
            if (paused.Problem != null) return (false, safety, paused.Problem);
            RunResult r;
            await using (paused)
            {
                try
                {
                    await using var conn = new NpgsqlConnection(ConnectionString(login));
                    await conn.OpenAsync();
                    await using var drop = new NpgsqlCommand(SetViewsAsideSql, conn);
                    await drop.ExecuteNonQueryAsync();
                }
                catch (Exception e) when (e is NpgsqlException or InvalidOperationException)
                {
                    return (false, safety, $"Nothing was restored: the fleet views couldn't be set aside first. {e.Message}");
                }
                r = await Run(tools.Restore, ConnectionArgs(login).Concat(new[]
                {
                    "-d", login.Database, "--clean", "--if-exists", "--single-transaction", "--no-owner", "--no-privileges", file,
                }), login);
            }
            WriteLog(s.Folder, new[]
            {
                r.Ok ? $"Restored {Path.GetFileName(file)} (the database before it is {Path.GetFileName(safety.File)})"
                     : $"RESTORE FAILED, nothing changed: {ToolError("pg_restore", r)}",
            });
            if (!r.Ok) return (false, safety, $"{ToolError("Restoring", r)} Nothing was changed.");
            return (true, safety, $"Restored. The database as it was just before is saved as {Path.GetFileName(safety.File)}.");
        }
    }
}
