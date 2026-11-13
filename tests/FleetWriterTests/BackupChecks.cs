using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using XIVFleetCompanion;

// The shared database's backups (PgBackups), as the app makes them: its settings file read and written as the app
// writes it, its checks on the settings, the backup names and which are kept, when the daily one is due, and a real
// backup, test, check and restore with PostgreSQL's own tools on a scratch database.
internal static partial class Program
{
    private static async Task BackupChecks(NpgsqlConnection admin, string given)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"backups-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            // The app's own file (backups/settings.js wrote this), read as it is.
            var file = Path.Combine(folder, "backups.json");
            File.WriteAllText(file, "{\n  \"enabled\": true,\n  \"folder\": \"E:\\\\FleetBackups\",\n  \"copyFolder\": \"\\\\\\\\LAPTOP\\\\Fleet\",\n  \"keep\": 7,\n  \"time\": \"04:15\",\n  \"pgBin\": \"\",\n  \"last\": {\n    \"ok\": true,\n    \"at\": 1794000000000,\n    \"message\": \"Backed up (1.2 MB).\",\n    \"file\": \"E:\\\\FleetBackups\\\\fleet-20261113-041500.dump\"\n  }\n}");
            var (s, last) = PgBackups.Load(file);
            Check(s == new PgBackups.Settings(true, "E:\\FleetBackups", "\\\\LAPTOP\\Fleet", 7, "04:15", "") && last is { Ok: true, At: 1794000000000, Message: "Backed up (1.2 MB)." },
                $"backups: the app's settings file read as the app wrote it: {s} {last}");
            PgBackups.Save(file, s with { Keep = 9 });
            var again = PgBackups.Load(file);
            Check(again.Settings.Keep == 9 && again.Last?.At == 1794000000000, "backups: saving the settings keeps the last result (and the app reads the same names back)");
            Check(PgBackups.Load(Path.Combine(folder, "none.json")).Settings == PgBackups.Settings.Defaults, "backups: no file yet gives the app's defaults (off, keep 14, 03:30)");

            // The app's answers for the same settings (backups/settings.js validateBackupSettings).
            var d = PgBackups.Settings.Defaults;
            var cases = new (PgBackups.Settings In, string? Error)[]
            {
                (d with { Enabled = true }, "Choose a folder for the backups first."),
                (d with { Folder = "rel\\x" }, "\"rel\\x\" is not a full folder path (for example E:\\FleetBackups or \\\\LAPTOP\\FleetBackups)."),
                (d with { Folder = "C:\\a", CopyFolder = "c:\\A\\" }, "The second copy needs a different folder (ideally on another PC or drive)."),
                (d with { Keep = 0 }, "Keep between 1 and 365 backups."),
                (d with { Time = "25:00" }, "Choose a time such as 03:30."),
                (d with { Folder = "C:\\a", Keep = 3, Time = "01:00" }, null),
            };
            Check(cases.All(c => PgBackups.Validate(c.In).Error == c.Error),
                "backups: settings checked as the app checks them: " + string.Join(" | ", cases.Select(c => PgBackups.Validate(c.In).Error ?? "ok")));

            // Names, and which are kept.
            Check(PgBackups.BackupName(new DateTime(2026, 11, 13, 4, 5, 6)) == "fleet-20261113-040506.dump" && PgBackups.TakenAt("fleet-20261113-040506.dump") == new DateTime(2026, 11, 13, 4, 5, 6)
                  && PgBackups.TakenAt("fleet-2026-11-13.db") == null && PgBackups.TakenAt("notes.txt") == null,
                "backups: named as the app names them, and only those count as backups");
            var made = Enumerable.Range(1, 5).Select(i => new PgBackups.Backup(PgBackups.BackupName(new DateTime(2026, 11, i)), "", new DateTime(2026, 11, i), 1)).ToList();
            Check(string.Join(",", PgBackups.ToRemove(made, 3).Select(b => b.TakenAt.Day)) == "2,1", "backups: beyond the newest kept, the oldest go");

            // When the daily one is due: none since its last due time, made by either the app's task or the plugin.
            var on = d with { Enabled = true, Folder = "C:\\b", Time = "03:30" };
            var at = new DateTime(2026, 11, 13, 10, 0, 0);
            Check(PgBackups.Due(on, new List<PgBackups.Backup>(), at)
                  && !PgBackups.Due(on, made.Append(new PgBackups.Backup("x", "", new DateTime(2026, 11, 13, 3, 31, 0), 1)), at)
                  && PgBackups.Due(on, made.Append(new PgBackups.Backup("x", "", new DateTime(2026, 11, 13, 3, 29, 0), 1)), at)
                  && !PgBackups.Due(on, made.Append(new PgBackups.Backup("x", "", new DateTime(2026, 11, 12, 3, 31, 0), 1)), new DateTime(2026, 11, 13, 2, 0, 0))
                  && !PgBackups.Due(on with { Enabled = false }, new List<PgBackups.Backup>(), at),
                "backups: the daily one is due when none was made since its time (yesterday's, before today's time), and only when on");
            Check(PgBackups.ServicePathsToFolders("\"C:\\Program Files\\PostgreSQL\\16\\bin\\pg_ctl.exe\" runservice -N \"postgresql-x64-16\"\r\n").SequenceEqual(new[] { "C:\\Program Files\\PostgreSQL\\16\\bin" }),
                "backups: a PostgreSQL service's folder found from its command, as the app finds it");

            // Get the backup tools: the download for the database's version, and only the tools' files kept from the zip.
            Check(PgBackups.ChooseDownload(160004).File?.Version == "16.15" && PgBackups.ChooseDownload(130010).Message == "The database runs PostgreSQL 13. The tools can be fetched for PostgreSQL 14 to 18 only.",
                "backups: Get the backup tools picks the database's version, and says when there is none");
            var zip = Path.Combine(folder, "pg.zip");
            using (var archive = System.IO.Compression.ZipFile.Open(zip, System.IO.Compression.ZipArchiveMode.Create))
                foreach (var name in new[] { "pgsql/bin/pg_dump.exe", "pgsql/bin/pg_restore.exe", "pgsql/bin/libpq.dll", "pgsql/bin/libssl-3-x64.dll", "pgsql/bin/psql.exe", "pgsql/bin/postgres.exe", "pgsql/commandlinetools_3rd_party_licenses.txt", "pgsql/share/x.txt" })
                    using (var w = new StreamWriter(archive.CreateEntry(name).Open())) w.Write(name);
            PgBackups.ExtractTools(zip, Path.Combine(folder, "tools"));
            Check(string.Join(",", Directory.GetFiles(Path.Combine(folder, "tools")).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal))
                  == "commandlinetools_3rd_party_licenses.txt,libpq.dll,libssl-3-x64.dll,pg_dump.exe,pg_restore.exe",
                "backups: from PostgreSQL's zip only pg_dump, pg_restore and what they need are kept (no server, no psql)");

            // A real backup, test, check and restore, on a scratch database with some of the fleet's tables.
            var pgBin = Directory.Exists("/usr/lib/postgresql/16/bin") ? "/usr/lib/postgresql/16/bin" : "";
            var (tools, toolsError) = await PgBackups.FindTools(pgBin, Path.Combine(folder, "no-tools"));
            if (tools == null)
            {
                Check(false, $"backups: PostgreSQL's tools found ({toolsError})");
                return;
            }
            await Exec(admin, "DROP DATABASE IF EXISTS backup_check WITH (FORCE)");
            await Exec(admin, "CREATE DATABASE backup_check");
            var b = new NpgsqlConnectionStringBuilder(given) { Database = "backup_check", Pooling = false };
            var login = new PgLogin(b.Host!, b.Port, "backup_check", b.Username!, b.Password ?? "");
            await using (var db = new NpgsqlConnection(b.ConnectionString))
            {
                await db.OpenAsync();
                await Exec(db, "CREATE TABLE companion_character_current (cid bigint PRIMARY KEY, name text); INSERT INTO companion_character_current VALUES (1, 'Ann'), (2, 'Bo')");
                await Exec(db, "CREATE TABLE companion_metric_history (id serial, v int); INSERT INTO companion_metric_history (v) SELECT g FROM generate_series(1, 50) g");
            }
            var backupsAt = Path.Combine(folder, "Backups");
            var copies = Path.Combine(folder, "Copies");
            var settings = d with { Folder = backupsAt, CopyFolder = copies, Keep = 2 };
            var first = await PgBackups.BackupNow(tools, login, settings, new DateTime(2026, 11, 13, 4, 0, 0));
            Check(first.Ok && File.Exists(first.File) && first.Copied != null && File.Exists(first.Copied) && first.Message.StartsWith("Backed up (") && first.Message.EndsWith("Second copy made."),
                $"backups: Back up now makes a complete backup, named as the app's, and the second copy: {first.Message}");
            var second = await PgBackups.BackupNow(tools, login, settings, new DateTime(2026, 11, 13, 4, 0, 0));
            await PgBackups.BackupNow(tools, login, settings, new DateTime(2026, 11, 13, 5, 0, 0));
            var kept = PgBackups.List(backupsAt);
            Check(second.Ok && Path.GetFileName(second.File) == "fleet-20261113-040001.dump" && kept.Count == 2 && PgBackups.List(copies).Count == 2
                  && kept[0].Name == "fleet-20261113-050000.dump" && File.ReadAllText(Path.Combine(backupsAt, "backup.log")).Contains("Removed old backup fleet-20261113-040000.dump"),
                "backups: a second in the same second moves on a second; only the newest kept, in both folders; and the log says so");
            Check(await PgBackups.CheckFile(tools, kept[0].File) == null && await PgBackups.CheckFile(tools, Path.Combine(folder, "backups.json")) is { } notOne && notOne.Contains("isn't a backup"),
                "backups: a backup file is recognised (Restore from a file); another file is refused");
            var test = await PgBackups.Test(tools, login, kept[0].File);
            Check(test.Ok && test.Rows.First(r => r.Table == "companion_metric_history").Restored == 50 && test.Rows.First(r => r.Table == "companion_inventory_snapshot").Live == null,
                $"backups: Test loads it into a scratch database and compares the counts: {test.Message}");

            await using (var db = new NpgsqlConnection(b.ConnectionString))
            {
                await db.OpenAsync();
                await Exec(db, "DELETE FROM companion_character_current WHERE cid = 2; INSERT INTO companion_metric_history (v) VALUES (99)");
            }
            var restore = await PgBackups.Restore(tools, login, settings, kept[0].File, new DateTime(2026, 11, 13, 6, 0, 0));
            await using (var db = new NpgsqlConnection(b.ConnectionString))
            {
                await db.OpenAsync();
                await using var count = new NpgsqlCommand("SELECT (SELECT count(*) FROM companion_character_current) || '/' || (SELECT count(*) FROM companion_metric_history)", db);
                var counts = (string)(await count.ExecuteScalarAsync())!;
                Check(restore.Ok && counts == "2/50" && restore.Safety?.File is { } safety && File.Exists(safety) && restore.Message.Contains(Path.GetFileName(safety)),
                    $"backups: Restore puts the database back as it was, after backing up how it was first: {restore.Message} ({counts})");
            }
            var missing = await PgBackups.Restore(tools, login, settings, Path.Combine(folder, "gone.dump"), DateTime.Now);
            Check(!missing.Ok && missing.Message.Contains("is not there"), "backups: a backup that's gone is said so, and nothing is done");
        }
        finally
        {
            try { await Exec(admin, "DROP DATABASE IF EXISTS backup_check WITH (FORCE)"); } catch { /* scratch */ }
            Directory.Delete(folder, true);
        }
    }
}
