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

        // ---- PostgreSQL's tools ----

        public sealed record Tools(string Bin, string Dump, string Restore, string Version, bool FromApp);

        public sealed record RunResult(bool Ok, int Code, string Stdout, string Stderr);

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
                return new RunResult(p.ExitCode == 0, p.ExitCode, await stdout, await stderr);
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
        public static async Task<(Tools? Tools, string? Error)> FindTools(string chosen, string appToolsDir)
        {
            var candidates = chosen.Length > 0 ? new List<string> { chosen } : (await ServiceFolders()).Concat(ProgramFilesFolders()).Append(appToolsDir).ToList();
            var bin = candidates.FirstOrDefault(f => f.Length > 0 && File.Exists(Path.Combine(f, Exe("pg_dump"))) && File.Exists(Path.Combine(f, Exe("pg_restore"))));
            if (chosen.Length > 0 && bin == null) return (null, $"pg_dump was not found in {chosen}. Choose PostgreSQL's \"bin\" folder.");
            var dump = bin != null ? Path.Combine(bin, Exe("pg_dump")) : Exe("pg_dump");
            var restore = bin != null ? Path.Combine(bin, Exe("pg_restore")) : Exe("pg_restore");
            var v = await Run(dump, new[] { "--version" });
            if (!v.Ok)
                return (null, "PostgreSQL's backup tools (pg_dump) are not on this PC. Get them with Get the backup tools, or make the backups on the PC that has the database.");
            return (new Tools(bin ?? "", dump, restore, v.Stdout.Trim(), bin != null && bin == appToolsDir), null);
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

        /// <summary>Puts the database back as it was in a backup: first a backup of how it is now (so this can be undone),
        /// then pg_restore --clean in one transaction, so a failure part way changes nothing.</summary>
        public static async Task<(bool Ok, BackupResult? Safety, string Message)> Restore(Tools tools, PgLogin login, Settings s, string file, DateTime now)
        {
            if (!File.Exists(file)) return (false, null, $"{file} is not there any more.");
            // Removes no old backups: the one being restored may be the oldest. The next daily backup tidies up.
            var safety = await BackupNow(tools, login, s with { CopyFolder = "", Keep = int.MaxValue }, now);
            if (!safety.Ok) return (false, safety, $"Nothing was restored: first backing up the database as it is now failed. {safety.Message}");
            var r = await Run(tools.Restore, ConnectionArgs(login).Concat(new[]
            {
                "-d", login.Database, "--clean", "--if-exists", "--single-transaction", "--no-owner", "--no-privileges", file,
            }), login);
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
