using System;
using System.Collections.Generic;
using System.Data.Common;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using XIVFleetCompanion;

// Step 4: moving the fleet between the fleet file and a shared database (FleetMove). The twin test's made-up fleet (the
// same in a PostgreSQL database and a file) is moved from the file into an empty shared database, then from there into a
// new file and over the old one; every page must read the same as before at each step.
internal static partial class Program
{
    private static async Task MoveChecks(NpgsqlConnection admin, string given, NpgsqlConnection twinPg, DbConnection twinFile,
        Func<DbConnection, DbConnection, string, Task> compare)
    {
        var files = FileDatabase.Files.Embedded();
        var pgFiles = DatabaseUpkeep.Files.Embedded();
        var folder = Path.Combine(Path.GetTempPath(), $"move-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        await Exec(admin, "DROP DATABASE IF EXISTS move_pg WITH (FORCE)");
        await Exec(admin, "CREATE DATABASE move_pg");
        try
        {
            await using var shared = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(given) { Database = "move_pg", Pooling = false }.ConnectionString);
            await shared.OpenAsync();
            // The twin's file was made from the tables file directly; the plugin's own upkeep gives it its notes table.
            await FileDatabase.EnsureAsync(twinFile, files, "move test");
            var characters = await FleetMove.CharacterCount(twinFile);

            // ---- The file into an empty shared database ----
            var steps = new List<string>();
            var there = await FleetMove.FileToSharedAsync(twinFile, shared, files, pgFiles, "move test", "move_pg on the test PC", steps.Add);
            var tables = FleetMove.TablesToMove(files);
            Check(there.Tables.Count == tables.Count && there.Rows >= 40 && steps.Contains("Checking every table arrived...") && there.Text.Contains("every table matches"),
                $"move: the file's fleet goes into an empty shared database, every table checked ({there.Text})");
            Check(!tables.Contains("housing_key_city_lookup") && !tables.Contains("companion_inventory_type_lookup") && tables.Contains("companion_metric_history"),
                $"move: every table moves except the lookups both already have ({tables.Count} tables)");
            Check(await FleetMove.CharacterCount(shared) == characters && characters > 0, $"move: all {characters} characters arrived");
            Check(await FleetMove.MovedAwayNote(twinFile) == "move_pg on the test PC" && await FleetMove.CharacterCount(twinFile) == characters,
                "move: the file is left with its fleet, and a note of where it went");
            await compare(twinPg, shared, "moved to shared");

            // Numbered tables carry on after what was copied.
            var maxRule = Convert.ToInt64(await new NpgsqlCommand("SELECT max(id) FROM sub_build_rules", shared).ExecuteScalarAsync());
            var newRule = await FleetEdits.AddRule(shared, new FleetEdits.Rule(null, 70, null, null, "S", "S", "S", "S"));
            Check(newRule == maxRule + 1, $"move: a Sub Rule added afterwards gets the next number ({newRule} after {maxRule})");
            await FleetEdits.DeleteRule(shared, newRule);

            // ---- Not into a database that already has a fleet ----
            var refused = "";
            try { await FleetMove.FileToSharedAsync(twinFile, shared, files, pgFiles, "move test", "again"); }
            catch (FleetMove.RefusedException ex) { refused = ex.Message; }
            Check(refused.Contains("already has a fleet") && await FleetMove.CharacterCount(shared) == characters && await FleetMove.MovedAwayNote(twinFile) == "move_pg on the test PC",
                $"move: never into a database that already has a fleet; nothing changes ({refused})");

            // ---- The shared database into a new file ----
            var newPath = Path.Combine(folder, "new", "fleet.db");
            await using (var newFile = await FileDatabase.OpenAsync(newPath, Array.Empty<string>()))
            {
                steps.Clear();
                var back = await FleetMove.SharedToFileAsync(shared, newFile, newPath, files, "move test", steps.Add);
                Check(back.Rows == there.Rows && back.Tables.Count == tables.Count, $"move: the shared database's fleet comes back into a new file, every table checked ({back.Text})");
                Check(await FleetMove.MovedAwayNote(newFile) == null, "move: the new file has no 'moved away' note");
                await compare(twinFile, newFile, "moved back to a file");

                // ---- Not over a file with a fleet of its own ----
                refused = "";
                try { await FleetMove.SharedToFileAsync(shared, newFile, newPath, files, "move test"); }
                catch (FleetMove.RefusedException ex) { refused = ex.Message; }
                Check(refused.Contains("already has a fleet file") && await FleetMove.CharacterCount(newFile) == characters,
                    $"move: never over a fleet file that has a fleet of its own ({refused})");
            }

            // ---- Back over the file it was moved away from: kept as a backup first ----
            var oldPath = Path.Combine(folder, "old", "fleet.db");
            await using (var oldFile = await FileDatabase.OpenAsync(oldPath, Array.Empty<string>()))
            {
                await FileDatabase.EnsureAsync(oldFile, files, "move test");
                await FleetWriter.WriteCurrentCharactersAsync(oldFile, new List<FleetWriter.CharacterSnapshot> { new() { Cid = 99, Name = "Stale", World = "Maduin" } });
                await Db.Exec(oldFile, "INSERT INTO companion_file_info (key, value) VALUES ('moved_to', 'somewhere')");
                var again = await FleetMove.SharedToFileAsync(shared, oldFile, oldPath, files, "move test");
                Check(again.Rows == there.Rows && FileDatabase.Backups(oldPath).Count == 1 && await FleetMove.MovedAwayNote(oldFile) == null
                      && await FleetMove.CharacterCount(oldFile) == characters && await Count(oldFile, "SELECT count(*) FROM companion_character_current WHERE cid = 99") == 0,
                    "move: back over a file whose fleet was moved away, it's backed up first, then holds only the moved fleet");
            }

            // ---- A copy that goes wrong changes nothing ----
            await Exec(admin, "DROP DATABASE IF EXISTS move_pg2 WITH (FORCE)");
            await Exec(admin, "CREATE DATABASE move_pg2");
            await using (var broken = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(given) { Database = "move_pg2", Pooling = false }.ConnectionString))
            {
                await broken.OpenAsync();
                await DatabaseUpkeep.EnsureAsync(broken, pgFiles, "move test", wait: true);
                // A value the database won't take, in a table copied late.
                await Exec(broken, "ALTER TABLE companion_market_listing ADD CONSTRAINT no_prices CHECK (unit_price < 0)");
                var failed = false;
                await Db.Exec(twinFile, "DELETE FROM companion_file_info WHERE key = 'moved_to'");
                try { await FleetMove.FileToSharedAsync(twinFile, broken, files, pgFiles, "move test", "move_pg2"); }
                catch (PostgresException) { failed = true; }
                Check(failed && await FleetMove.CharacterCount(broken) == 0 && await FleetMove.MovedAwayNote(twinFile) == null,
                    "move: a copy that fails part way leaves the shared database empty and the file without a note");
                var writable = await Db.Exec(twinFile, "UPDATE companion_file_info SET value = value WHERE key = 'app_version'") == 1;
                Check(await Count(twinFile, "SELECT count(*) FROM companion_character_current") == characters && writable,
                    "move: ...and the file as it was, and other games can write to it again");
            }
            await Exec(admin, "DROP DATABASE IF EXISTS move_pg2 WITH (FORCE)");

            // ---- Which way this PC goes, for every game on it ----
            var choicePath = FleetMove.ChoicePath(Path.Combine(folder, "pc", "fleet.db"));
            Check(FleetMove.ReadChoice(choicePath) == null, "pc choice: none until a game chooses or moves");
            var when = new DateTime(2026, 11, 7, 12, 0, 0, DateTimeKind.Utc);
            FleetMove.WriteChoice(choicePath, new FleetMove.PcChoice(FleetStoreChoice.SharedDatabase, true, "plugin test", when));
            var read = FleetMove.ReadChoice(choicePath);
            Check(read is { Store: FleetStoreChoice.SharedDatabase, UseRemote: true, ChangedBy: "plugin test" } && read.ChangedAt == when,
                "pc choice: what one game writes, the others read (shared, Remote, by whom and when)");
            FleetMove.WriteChoice(choicePath, new FleetMove.PcChoice(FleetStoreChoice.ThisPc, false, "plugin test", when));
            Check(FleetMove.ReadChoice(choicePath)?.Store == FleetStoreChoice.ThisPc && Path.GetFileName(choicePath) == "store.json",
                "pc choice: written again (store.json beside the fleet file)");
            File.WriteAllText(choicePath, "{ not json");
            Check(FleetMove.ReadChoice(choicePath) == null, "pc choice: a damaged one is ignored, not a crash");
        }
        finally
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch (IOException) { }
            await Exec(admin, "DROP DATABASE IF EXISTS move_pg WITH (FORCE)");
        }
    }
}
