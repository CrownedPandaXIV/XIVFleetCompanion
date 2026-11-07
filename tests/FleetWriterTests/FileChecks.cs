using System;
using System.Collections.Generic;
using System.Data.SQLite;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using XIVFleetCompanion;

// 2.2 "Just this PC": the fleet file itself (FileDatabase): where it may be kept, made and brought up to date, backed
// up, and shared by several games writing at the same moment (separate processes, as separate games are).
internal static partial class Program
{
    private static async Task FileChecks()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"fleetfile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            CloudFolderChecks();
            await MakeAndUpdateChecks(folder);
            await BackupChecks(folder);
            await KeeperOnFileChecks(folder);
            await TwoGamesAtOnceChecks(folder);
        }
        finally
        {
            SQLiteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch (IOException) { }
        }
    }

    private static void CloudFolderChecks()
    {
        var none = Array.Empty<string>();
        Check(FileDatabase.WhyNotHere(@"C:\Users\Panda\AppData\Roaming\XIV Fleet Companion\fleet.db".Replace('\\', Path.DirectorySeparatorChar), none) == null,
            "file: an ordinary AppData folder is fine");
        Check(FileDatabase.WhyNotHere("/home/Box/AppData/Roaming/XIV Fleet Companion/fleet.db", none) == null,
            "file: a user named like a cloud service isn't mistaken for one");
        Check(FileDatabase.WhyNotHere("/Users/Panda/OneDrive/AppData/XIV Fleet Companion/fleet.db", none)?.Contains("OneDrive") == true,
            "file: a OneDrive folder is refused, saying OneDrive");
        Check(FileDatabase.WhyNotHere("/Users/Panda/OneDrive - Contoso/XIV Fleet Companion/fleet.db", none)?.Contains("OneDrive") == true,
            "file: a work OneDrive folder ('OneDrive - Company') is refused");
        Check(FileDatabase.WhyNotHere("/Users/Panda/Dropbox/fleet/fleet.db", none)?.Contains("Dropbox") == true, "file: a Dropbox folder is refused");
        Check(FileDatabase.WhyNotHere("/Volumes/Google Drive/My Drive/fleet.db", none)?.Contains("Google Drive") == true, "file: a Google Drive folder is refused");
        Check(FileDatabase.WhyNotHere("/Users/Panda/Roaming/fleet.db", new[] { "/Users/Panda/Roaming" })?.Contains("OneDrive") == true,
            "file: a folder OneDrive says it syncs (its own setting, whatever the folder is called) is refused");
        Check(FileDatabase.WhyNotHere("/Users/Panda/RoamingOther/fleet.db", new[] { "/Users/Panda/Roaming" }) == null,
            "file: a folder that only starts with the same letters as a synced one is fine");
        if (OperatingSystem.IsWindows())
            Check(FileDatabase.WhyNotHere(@"\\nas\share\fleet.db", none)?.Contains("network") == true, "file: a network folder is refused");
        Check(FileDatabase.DefaultPath.EndsWith(Path.Combine("XIV Fleet Companion", "fleet.db")), $"file: the shared place is ...\\XIV Fleet Companion\\fleet.db ({FileDatabase.DefaultPath})");
    }

    private static async Task<long> Count(System.Data.Common.DbConnection conn, string sql)
    {
        await using var cmd = Db.Command(conn, sql);
        return Convert.ToInt64(await cmd.ExecuteScalarAsync());
    }

    private static async Task MakeAndUpdateChecks(string folder)
    {
        var refused = false;
        try
        {
            await using var _ = await FileDatabase.OpenAsync(Path.Combine(folder, "Dropbox", "fleet.db"), Array.Empty<string>());
        }
        catch (FileDatabase.RefusedException) { refused = true; }
        Check(refused && !Directory.Exists(Path.Combine(folder, "Dropbox")), "file: opening one in a cloud folder is refused before anything is made there");

        var path = Path.Combine(folder, "made", "fleet.db");
        var files = FileDatabase.Files.Embedded();
        await using var conn = await FileDatabase.OpenAsync(path, Array.Empty<string>());
        Check(File.Exists(path), "file: opening makes the file, and its folder");
        Check((string)(await Db.Command(conn, "PRAGMA journal_mode").ExecuteScalarAsync())! == "wal", "file: it's opened so games can read while another writes (write-ahead log)");

        var first = await FileDatabase.EnsureAsync(conn, files, "test");
        var tables = await Count(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name <> 'companion_file_info'");
        var expected = System.Text.RegularExpressions.Regex.Matches(files.Tables, @"^CREATE TABLE", System.Text.RegularExpressions.RegexOptions.Multiline).Count;
        var views = await Count(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'view'");
        Check(first.Created && !first.Newer && tables == expected && views >= 30, $"file: a new file gets every table and view ({tables} tables, {views} views): {first.Text}");
        var again = await FileDatabase.EnsureAsync(conn, files, "test");
        Check(!again.Created && again.Applied.Count == 0 && again.Text == "The fleet file is up to date.", $"file: looking again changes nothing: {again.Text}");
        Check(await Count(conn, $"SELECT count(*) FROM companion_file_info WHERE key = 'app_version' AND value = '{files.AppVersion}'") == 1,
            "file: it records which app version its tables and views are from");

        // A fleet in it, then an older layout: a column, a table and an index missing, older views.
        await FleetWriter.WriteCurrentCharactersAsync(conn, new List<FleetWriter.CharacterSnapshot>
        {
            new() { Cid = 42, Name = "Kept", World = "Maduin", Gil = 5, AccountLabel = "a", FcId = 9232379236109516865 },
        });
        var index = (string)(await Db.Command(conn, "SELECT name FROM sqlite_master WHERE type = 'index' AND sql IS NOT NULL ORDER BY name LIMIT 1").ExecuteScalarAsync())!;
        await Db.Exec(conn, $"DROP INDEX \"{index}\"");
        var viewNames = new List<string>();
        await using (var cmd = Db.Command(conn, "SELECT name FROM sqlite_master WHERE type = 'view'"))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) viewNames.Add(r.GetString(0));
        foreach (var v in viewNames) await Db.Exec(conn, $"DROP VIEW \"{v}\"");
        await Db.Exec(conn, "ALTER TABLE companion_character_current DROP COLUMN num_sub_slots");
        await Db.Exec(conn, "DROP TABLE companion_metric_latest");
        await Db.Exec(conn, "UPDATE companion_file_info SET value = '1.8.0' WHERE key = 'app_version'");
        await Db.Exec(conn, "UPDATE companion_file_info SET value = 'old' WHERE key = 'views_checksum'");
        var updated = await FileDatabase.EnsureAsync(conn, files, "test");
        Check(updated.Applied.Contains("added companion_metric_latest") && updated.Applied.Contains("added companion_character_current.num_sub_slots")
              && updated.Applied.Contains($"added {index}") && updated.Applied.Contains("the fleet views"),
            $"file: an older file gets the missing table, column, index and the new views: {updated.Text}");
        Check(await Count(conn, "SELECT count(*) FROM sqlite_master WHERE name = 'fleet_subs_view'") == 1
              && await Count(conn, "SELECT count(*) FROM companion_character_current WHERE cid = 42 AND name = 'Kept'") == 1,
            "file: ...and keeps the fleet in it");
        Check((await FleetReader.ReadSubs(conn)) != null, "file: the pages read it afterwards");

        // A newer plugin in another game already updated it: left alone.
        await Db.Exec(conn, "UPDATE companion_file_info SET value = '99.0.0' WHERE key = 'app_version'");
        await Db.Exec(conn, "DROP VIEW fleet_subs_view");
        var newer = await FileDatabase.EnsureAsync(conn, files, "test");
        Check(newer.Newer && newer.Text.Contains("newer plugin") && await Count(conn, "SELECT count(*) FROM sqlite_master WHERE name = 'fleet_subs_view'") == 0,
            $"file: one a newer plugin updated is left as it is, and says to update: {newer.Text}");

        // Views that fail part way: nothing changes.
        await Db.Exec(conn, "UPDATE companion_file_info SET value = '1.8.0' WHERE key = 'app_version'");
        var viewsBefore = await Count(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'view'");
        var threw = false;
        try { await FileDatabase.EnsureAsync(conn, files with { Views = files.Views + "\nCREATE VIEW broken AS SELEC 1;" }, "test"); }
        catch (InvalidOperationException ex) { threw = ex.Message.Contains("nothing in it was changed"); }
        Check(threw && await Count(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'view'") == viewsBefore
              && await Count(conn, "SELECT count(*) FROM companion_file_info WHERE key = 'app_version' AND value = '1.8.0'") == 1,
            "file: an update that fails part way leaves the file as it was, and says so");
    }

    private static async Task BackupChecks(string folder)
    {
        var path = Path.Combine(folder, "backups", "fleet.db");
        await using var conn = await FileDatabase.OpenAsync(path, Array.Empty<string>());
        await FileDatabase.EnsureAsync(conn, FileDatabase.Files.Embedded(), "test");
        await FleetWriter.WriteCurrentCharactersAsync(conn, new List<FleetWriter.CharacterSnapshot> { new() { Cid = 7, Name = "Backed", World = "Maduin" } });
        var day = new DateTime(2025, 3, 7, 9, 30, 0);
        Check(FileDatabase.BackupDue(path, day), "backup: due when none was made today");
        var made = await FileDatabase.BackUpAsync(conn, path, day);
        Check(Path.GetFileName(made) == "fleet-2025-03-07.db" && File.Exists(made), $"backup: named by the day ({Path.GetFileName(made)}), in Backups beside the file");
        await using (var copy = new SQLiteConnection($"Data Source={made};Pooling=False;Read Only=True"))
        {
            await copy.OpenAsync();
            Check(await Count(copy, "SELECT count(*) FROM companion_character_current WHERE name = 'Backed'") == 1, "backup: the copy has the fleet and opens on its own");
        }
        Check(!FileDatabase.BackupDue(path, DateTime.Now), "backup: not due again the same day");
        var second = await FileDatabase.BackUpAsync(conn, path, day.AddHours(1));
        Check(Path.GetFileName(second) == "fleet-2025-03-07-103000.db", $"backup: Back up now the same day keeps both ({Path.GetFileName(second)})");
        // As if made on those days, so the eight more below are newer.
        File.SetLastWriteTime(made, day);
        File.SetLastWriteTime(second, day.AddHours(1));
        for (var i = 1; i <= 8; i++)
        {
            var at = day.AddDays(i);
            var f = await FileDatabase.BackUpAsync(conn, path, at);
            File.SetLastWriteTime(f, at);
        }
        var kept = FileDatabase.Backups(path);
        Check(kept.Count == FileDatabase.BackupsKept && kept[0].Name == "fleet-2025-03-15.db" && kept.All(f => f.Name.CompareTo("fleet-2025-03-09") >= 0),
            $"backup: the newest {FileDatabase.BackupsKept} are kept ({string.Join(", ", kept.Select(f => f.Name))})");
        Check(!Directory.GetFiles(FileDatabase.BackupFolder(path), "*.tmp").Any(), "backup: no half-made copies are left behind");
    }

    private static async Task KeeperOnFileChecks(string folder)
    {
        var path = Path.Combine(folder, "keeper", "fleet.db");
        var logged = new List<string>();
        var keeper = new DatabaseKeeper(() => throw new InvalidOperationException("the PostgreSQL files aren't for the file"), "plugin test", logged.Add);
        await using var conn = await FileDatabase.OpenAsync(path, Array.Empty<string>());
        await keeper.RunIfDueAsync(conn, path);
        Check(keeper.Problem == null && keeper.LastFile is { Created: true } && keeper.StatusText == "Made the fleet file." && logged.Contains("Made the fleet file."),
            $"keeper: on the file it makes it and says so ({keeper.StatusText})");
        Check(keeper.LastBackup != null && FileDatabase.Backups(path).Count == 1, "keeper: and backs it up, as nothing was backed up today");
        Check((await FleetReader.ReadSubs(conn)) != null, "keeper: the pages read the file it made");
    }

    // Several games on one PC sync to the same file at once: separate processes write at the same moment, each making the
    // file ready first as the plugin does, and nothing is lost or refused.
    private static async Task TwoGamesAtOnceChecks(string folder)
    {
        var path = Path.Combine(folder, "shared", "fleet.db");
        const int each = 60;
        var self = Environment.ProcessPath!;
        var dll = typeof(Program).Assembly.Location;
        ProcessStartInfo Start(int seed)
        {
            var info = new ProcessStartInfo(self) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            if (Path.GetFileNameWithoutExtension(self) == "dotnet") info.ArgumentList.Add(dll);
            foreach (var a in new[] { "--hammer", path, seed.ToString(), each.ToString() }) info.ArgumentList.Add(a);
            return info;
        }
        var games = new[] { 1, 2, 3 }.Select(seed => Process.Start(Start(seed))!).ToList();
        var here = Hammer(path, 4, each);
        var outputs = await Task.WhenAll(games.Select(async g =>
        {
            var output = await g.StandardOutput.ReadToEndAsync() + await g.StandardError.ReadToEndAsync();
            await g.WaitForExitAsync();
            return (g.ExitCode, output);
        }));
        var hereCode = await here;
        Check(outputs.All(o => o.ExitCode == 0) && hereCode == 0,
            "two games at once: 4 processes (3 separate) made the file ready and synced at the same moment without an error" +
            string.Concat(outputs.Where(o => o.ExitCode != 0).Select(o => $" | {o.output.Trim()}")));
        await using var conn = await FileDatabase.OpenAsync(path, Array.Empty<string>());
        var characters = await Count(conn, "SELECT count(*) FROM companion_character_current");
        var history = await Count(conn, "SELECT count(*) FROM companion_metric_history");
        Check(characters == 4 * each && history == 4 * each, $"two games at once: every write is there ({characters} characters, {history} history rows; {4 * each} written)");
        Check(await Count(conn, "SELECT count(*) FROM companion_file_info WHERE key = 'app_version'") == 1
              && await Count(conn, "SELECT count(*) FROM sqlite_master WHERE type = 'view'") >= 30,
            "two games at once: the file was made once, with its views");
        Check((string)(await Db.Command(conn, "PRAGMA integrity_check").ExecuteScalarAsync())! == "ok", "two games at once: the file is whole afterwards (integrity check)");
    }

    // One game: makes the file ready, then syncs `count` characters one sync at a time, reading in between.
    private static async Task<int> Hammer(string path, int seed, int count)
    {
        try
        {
            await using var conn = await FileDatabase.OpenAsync(path, Array.Empty<string>());
            await FileDatabase.EnsureAsync(conn, FileDatabase.Files.Embedded(), $"game {seed}");
            for (var i = 0; i < count; i++)
            {
                var cid = (ulong)(seed * 100_000 + i);
                await FleetWriter.WriteCurrentCharactersAsync(conn, new List<FleetWriter.CharacterSnapshot>
                {
                    new() { Cid = cid, Name = $"G{seed} C{i}", World = "Maduin", Gil = i, AccountLabel = $"game{seed}", FcId = 9232379236109516865 },
                });
                await MetricWriter.WriteMetricsAsync(conn, new List<MetricWriter.MetricPoint>
                {
                    new() { SubjectType = "character", SubjectId = cid, Metric = "gil", Value = i },
                });
                if (i % 10 == 0) await FleetReader.ReadSubs(conn);
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"game {seed}: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
}
