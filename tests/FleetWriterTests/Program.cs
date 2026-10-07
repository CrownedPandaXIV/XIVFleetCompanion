// Checks the plugin's database writes (FleetWriter.cs) against a scratch Postgres: every table is
// created in a throwaway schema with the same column types as the real database, written to,
// read back and compared. Never point this at the real database.
//
//   FLEET_TEST_DB="Host=localhost;Username=postgres;Password=postgres;Database=postgres" dotnet run
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using XIVFleetCompanion;

internal static class Program
{
    private static int failures;
    private static int passes;

    private static void Check(bool ok, string what)
    {
        if (ok) passes++; else failures++;
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {what}");
    }

    private const string Schema = "fleetwriter_test";

    // Column types as in the real database.
    private const string CreateTables = @"
        CREATE TABLE companion_character_snapshot (cid numeric NOT NULL, name text, world text, account_label text,
            snapshot_at timestamptz NOT NULL DEFAULT now(), retainer_count int, submarine_count int, gil bigint,
            ceruleum int, repair_kits int, fc_id numeric, num_sub_slots int);
        CREATE TABLE companion_inventory_snapshot (owner_cid numeric NOT NULL, retainer_id numeric NOT NULL,
            sorted_container int NOT NULL, sorted_slot_index int NOT NULL, item_id int, quantity int,
            updated_at timestamptz NOT NULL DEFAULT now(), gear_set_ids int[],
            PRIMARY KEY (owner_cid, retainer_id, sorted_container, sorted_slot_index));
        CREATE TABLE companion_fc_inventory_snapshot (fc_id numeric NOT NULL, sorted_container int NOT NULL,
            sorted_slot_index int NOT NULL, item_id int NOT NULL, quantity int NOT NULL,
            updated_at timestamp NOT NULL DEFAULT now(), PRIMARY KEY (fc_id, sorted_container, sorted_slot_index));
        CREATE TABLE companion_submarine_snapshot (cid numeric NOT NULL, sub_name text NOT NULL, level int,
            part1 int, part2 int, part3 int, part4 int, points bytea, return_time bigint,
            updated_at timestamp NOT NULL DEFAULT now(), current_exp bigint, next_level_exp bigint,
            PRIMARY KEY (cid, sub_name));
        CREATE TABLE companion_retainer_lookup (retainer_id numeric NOT NULL PRIMARY KEY, owner_cid numeric NOT NULL,
            name text NOT NULL, updated_at timestamp NOT NULL DEFAULT now(), job int, gil bigint, has_venture boolean,
            venture_id int, venture_begins_at bigint, venture_ends_at bigint, level int, hire_order_index int);
        CREATE TABLE companion_character_housing (cid numeric NOT NULL PRIMARY KEY, fc_id numeric, fc_name text,
            fc_points int, fc_rank text, total_members int, has_house boolean, house_city int, house_ward int,
            house_plot int, house_last_visited timestamptz, updated_at timestamptz NOT NULL DEFAULT now(),
            fc_master text, fc_home_world_id int, fc_founding_date timestamptz, fc_eligibility_override timestamptz);";

    // A file in the repository (the sql/ scripts), found by walking up from the test program.
    private static string RepoFile(string relative)
    {
        for (var dir = new System.IO.DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        {
            var path = System.IO.Path.Combine(dir.FullName, relative);
            if (System.IO.File.Exists(path)) return path;
        }
        throw new InvalidOperationException($"Could not find {relative} above {AppContext.BaseDirectory}");
    }

    private static async Task RunScript(NpgsqlConnection conn, string relative)
    {
        await using var cmd = new NpgsqlCommand(await System.IO.File.ReadAllTextAsync(RepoFile(relative)), conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task Exec(NpgsqlConnection conn, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<object?[]>> Rows(NpgsqlConnection conn, string sql)
    {
        var list = new List<object?[]>();
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            list.Add(row);
        }
        return list;
    }

    private static string Show(List<object?[]> rows)
        => string.Join(" / ", rows.Select(r => string.Join(",", r.Select(v => v switch
        {
            null => "null",
            int[] a => "{" + string.Join(",", a) + "}",
            byte[] b => "0x" + Convert.ToHexString(b),
            _ => Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture),
        }))));

    private static async Task<int> Main()
    {
        var connectionString = Environment.GetEnvironmentVariable("FLEET_TEST_DB")
            ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using (var setup = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE; CREATE SCHEMA {Schema}; SET search_path TO {Schema}; {CreateTables}", conn))
            await setup.ExecuteNonQueryAsync();

        try
        {
            await RunChecks(conn);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE", conn);
            await drop.ExecuteNonQueryAsync();
        }

        Console.WriteLine(failures == 0 ? $"ALL {passes} CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    private static async Task RunChecks(NpgsqlConnection conn)
    {
        // The old per-sync history, as an older plugin wrote it (plugin 0.4.0 no longer does), for
        // the sql/004 and sql/005 checks.
        await Exec(conn, @"
            INSERT INTO companion_character_snapshot (cid, name, world, retainer_count, submarine_count, gil, ceruleum, repair_kits, account_label, fc_id, num_sub_slots) VALUES
                (18014498578000001, 'Aki', 'Maduin', 10, 4, 4000000000, 5000, 300, 'Main', 9000001, 4),
                (18014498578000002, 'Bex', 'Behemoth', 0, 0, 0, 0, 0, NULL, NULL, 0)");

        // sql/004: current rows and the history worth keeping, copied from the snapshot history.
        await Exec(conn, @"
            INSERT INTO companion_character_snapshot (cid, name, world, account_label, retainer_count, submarine_count, gil, ceruleum, repair_kits, fc_id, num_sub_slots, snapshot_at) VALUES
              (18014498578000001, 'Old Aki', 'Maduin', 'Main', 8, 4, 1, 1, 1, NULL,    3, now() - interval '30 days'),
              (18014498578000001, 'Old Aki', 'Maduin', 'Main', 8, 4, 2, 2, 2, NULL,    3, now() - interval '29 days'),
              (18014498578000001, 'Aki',     'Maduin', 'Main', 10, 4, 3, 3, 3, 9000001, 4, now() - interval '10 days')");
        await RunScript(conn, "sql/001_metric_history.sql");
        await RunScript(conn, "sql/004_character_current.sql");
        var current = await Rows(conn, "SELECT cid, name, retainer_count, num_sub_slots, fc_id, first_seen_at < now() - interval '29 days', last_synced_at > now() - interval '1 hour' FROM companion_character_current ORDER BY cid");
        Check(Show(current) == "18014498578000001,Aki,10,4,9000001,True,True / 18014498578000002,Bex,0,0,null,False,True",
            "004 fills the current table from each character's newest snapshot, first seen from the oldest: " + Show(current));
        var changeLog = await Rows(conn, "SELECT field, old_value, new_value FROM companion_character_changes ORDER BY field");
        Check(Show(changeLog) == "fc_id,null,9000001 / name,Old Aki,Aki", "004 copies past name and Free Company changes into the change log: " + Show(changeLog));
        var counts004 = await Rows(conn, "SELECT metric, string_agg(value::text, '>' ORDER BY recorded_at) FROM companion_metric_history WHERE subject_id = 18014498578000001 GROUP BY metric ORDER BY metric");
        Check(Show(counts004) == "num_sub_slots,3>4 / retainer_count,8>10 / submarine_count,4",
            "004 copies retainer, submarine and sub slot changes into the chart history: " + Show(counts004));
        const string countsSql = "SELECT (SELECT count(*) FROM companion_character_current), (SELECT count(*) FROM companion_character_changes), (SELECT count(*) FROM companion_metric_history), (SELECT count(*) FROM companion_metric_latest)";
        var firstRun = Show(await Rows(conn, countsSql));
        await RunScript(conn, "sql/004_character_current.sql");
        var secondRun = Show(await Rows(conn, countsSql));
        Check(firstRun == secondRun && firstRun == "2,2,8,6", $"running 004 again adds nothing (current, changes, history, latest): {firstRun} then {secondRun}");

        // Current rows from the plugin: inserted or updated in one statement; identity changes are logged.
        await FleetWriter.WriteCurrentCharactersAsync(conn, new[]
        {
            new FleetWriter.CharacterSnapshot { Cid = 18014498578000001, Name = "Aki", World = "Maduin", RetainerCount = 10, SubmarineCount = 4,
                Gil = 5_000_000_000, Ceruleum = 4000, RepairKits = 250, AccountLabel = "Main", FcId = 9000001, NumSubSlots = 4 },
            new FleetWriter.CharacterSnapshot { Cid = 18014498578000002, Name = "Bex Renamed", World = "Behemoth", AccountLabel = "", FcId = 9000002 },
            new FleetWriter.CharacterSnapshot { Cid = 18014498578000003, Name = "Cal", World = "Cuchulainn", AccountLabel = "Alt" },
        });
        current = await Rows(conn, "SELECT cid, name, gil, ceruleum, account_label, fc_id FROM companion_character_current ORDER BY cid");
        Check(Show(current) == "18014498578000001,Aki,5000000000,4000,Main,9000001 / 18014498578000002,Bex Renamed,0,0,null,9000002 / 18014498578000003,Cal,0,0,Alt,null",
            "current rows are updated and a new character is added: " + Show(current));
        changeLog = await Rows(conn, "SELECT cid, field, old_value, new_value FROM companion_character_changes WHERE changed_at > now() - interval '1 minute' ORDER BY cid, field");
        Check(Show(changeLog) == "18014498578000002,fc_id,null,9000002 / 18014498578000002,name,Bex,Bex Renamed",
            "a rename and a new Free Company are logged; gil changes and new characters are not: " + Show(changeLog));

        // Inventory: per source (bags, each retainer), gear sets kept as int arrays.
        const ulong owner = 18014498578000001;
        const ulong retA = 33777097243660301, retB = 33777097243660302;
        FleetWriter.InventorySource Seen(ulong id, params FleetWriter.InventoryItem[] items) => new() { Id = id, Name = $"src{id % 100}", SeenItems = items.ToList() };
        FleetWriter.InventorySource Unseen(ulong id) => new() { Id = id, Name = $"Ret{id % 100}" };
        FleetWriter.InventoryItem Item(uint container, int slot, uint item, uint qty, params uint[] gear) =>
            new() { RetainerId = 0, SortedContainer = container, SortedSlotIndex = slot, ItemId = item, Quantity = qty, GearSetIds = gear };

        var plan = FleetWriter.PlanInventory(owner, new[]
        {
            Seen(owner, Item(0, 0, 22500, 12), Item(1000, 3, 40000, 1, 1, 4)),
            Seen(retA, Item(10000, 7, 21792, 2), Item(10000, 8, 22500, 5)),
            Seen(retB, Item(10000, 1, 22501, 3)),
        }, retainerListKnown: true);
        await FleetWriter.WriteInventoryAsync(conn, owner, plan);
        var inv = await Rows(conn, "SELECT retainer_id, sorted_container, sorted_slot_index, item_id, quantity, gear_set_ids FROM companion_inventory_snapshot ORDER BY retainer_id, sorted_container, sorted_slot_index");
        Check(Show(inv) == $"{owner},0,0,22500,12,null / {owner},1000,3,40000,1,{{1,4}} / {retA},10000,7,21792,2,null / {retA},10000,8,22500,5,null / {retB},10000,1,22501,3,null",
            "items are stored under the source they were read from, with gear sets as arrays and none as empty: " + Show(inv));

        var totals = await FleetWriter.ReadItemTotalsAsync(conn, owner, new uint[] { 22500, 22501, 22502 });
        Check(totals[22500] == 17 && totals[22501] == 3 && totals[22502] == 0, $"stored item totals add bags and retainers, 0 when none: {totals[22500]},{totals[22501]},{totals[22502]}");

        // Retainer A not seen this time (AllaganTools answered with nothing): it keeps its items while
        // the bags and retainer B are replaced.
        plan = FleetWriter.PlanInventory(owner, new[] { Seen(owner, Item(0, 0, 22500, 10)), Unseen(retA), Seen(retB) }, retainerListKnown: true);
        Check(plan.BagsSeen && plan.Unseen.Count == 1 && plan.Unseen[0].Name == "Ret1" && string.Join(",", plan.Read) == $"{owner},{retB}",
            "the plan names the unseen retainer and reads only the seen sources");
        await FleetWriter.WriteInventoryAsync(conn, owner, plan);
        inv = await Rows(conn, "SELECT retainer_id, item_id, quantity FROM companion_inventory_snapshot ORDER BY retainer_id, sorted_slot_index");
        Check(Show(inv) == $"{owner},22500,10 / {retA},21792,2 / {retA},22500,5",
            "an unseen retainer keeps its last items; seen sources are replaced (retainer B, now empty, has none): " + Show(inv));

        Check(await FleetWriter.ReadStoredSourceCountAsync(conn, owner) == 2 && await FleetWriter.ReadStoredSourceCountAsync(conn, 1) == 0,
            "stored sources are counted per character (bags and retainer A; retainer B now holds nothing)");

        // Bags not seen either: nothing is read, everything is kept.
        plan = FleetWriter.PlanInventory(owner, new[] { Unseen(owner), Unseen(retA) }, retainerListKnown: true);
        Check(!plan.BagsSeen && plan.Read.Count == 0 && plan.Unseen.Count == 1, "unseen bags are not listed as a retainer, and nothing is read");

        // Retainer A dismissed (no longer in AutoRetainer's list): its items go.
        plan = FleetWriter.PlanInventory(owner, new[] { Seen(owner, Item(0, 0, 22500, 10)), Seen(retB, Item(10000, 1, 22501, 3)) }, retainerListKnown: true);
        await FleetWriter.WriteInventoryAsync(conn, owner, plan);
        inv = await Rows(conn, "SELECT retainer_id, item_id FROM companion_inventory_snapshot ORDER BY retainer_id");
        Check(Show(inv) == $"{owner},22500 / {retB},22501", "a dismissed retainer's items are removed: " + Show(inv));

        // Without AutoRetainer's retainer list, only what was read is replaced.
        await Exec(conn, $"INSERT INTO companion_inventory_snapshot (owner_cid, retainer_id, sorted_container, sorted_slot_index, item_id, quantity) VALUES ({owner}, {retA}, 10000, 0, 22502, 1)");
        plan = FleetWriter.PlanInventory(owner, new[] { Seen(owner, Item(0, 0, 22500, 9)) }, retainerListKnown: false);
        await FleetWriter.WriteInventoryAsync(conn, owner, plan);
        inv = await Rows(conn, "SELECT retainer_id, item_id, quantity FROM companion_inventory_snapshot ORDER BY retainer_id");
        Check(Show(inv) == $"{owner},22500,9 / {retA},22502,1 / {retB},22501,3", "with no retainer list, other sources are left alone: " + Show(inv));

        await FleetWriter.WriteInventoryAsync(conn, 18014498578000002, FleetWriter.PlanInventory(18014498578000002,
            new[] { Seen(18014498578000002, Item(0, 0, 22500, 1), Item(0, 1, 22501, 1)) }, retainerListKnown: true));
        var counts = await Rows(conn, "SELECT owner_cid, count(*) FROM companion_inventory_snapshot GROUP BY 1 ORDER BY 1");
        Check(Show(counts) == $"{owner},3 / 18014498578000002,2", "another character's inventory does not touch the first one: " + Show(counts));

        // A failed write leaves the old inventory in place (all or nothing).
        var duplicate = FleetWriter.PlanInventory(owner, new[] { Seen(owner, Item(0, 0, 22500, 1), Item(0, 0, 22500, 1)) }, retainerListKnown: true);
        var failed = false;
        try { await FleetWriter.WriteInventoryAsync(conn, owner, duplicate); } catch (PostgresException) { failed = true; }
        counts = await Rows(conn, $"SELECT count(*) FROM companion_inventory_snapshot WHERE owner_cid = {owner}");
        Check(failed && Show(counts) == "3", "a write that fails (two items in the same slot) changes nothing: " + Show(counts));

        Check(FleetWriter.Fingerprint(FleetWriter.PlanInventory(owner, new[] { Seen(owner), Unseen(retA) }, true))
              != FleetWriter.Fingerprint(FleetWriter.PlanInventory(owner, new[] { Seen(owner), Seen(retA) }, true)),
            "a retainer becoming seen (even with no items) changes the fingerprint, so it is written");

        // FC chest.
        const ulong fc = 9000001;
        await FleetWriter.WriteFcInventoryAsync(conn, fc, new List<FleetWriter.InventoryItem>
        {
            new() { SortedContainer = 20000, SortedSlotIndex = 0, ItemId = 10155, Quantity = 999 },
            new() { SortedContainer = 20002, SortedSlotIndex = 5, ItemId = 22504, Quantity = 3 },
        });
        await FleetWriter.WriteFcInventoryAsync(conn, fc, new List<FleetWriter.InventoryItem>
        {
            new() { SortedContainer = 20000, SortedSlotIndex = 0, ItemId = 10155, Quantity = 950 },
        });
        var chest = await Rows(conn, "SELECT fc_id, sorted_container, sorted_slot_index, item_id, quantity FROM companion_fc_inventory_snapshot");
        Check(Show(chest) == "9000001,20000,0,10155,950", "the FC chest is replaced as a whole: " + Show(chest));

        // A chest of an FC that no tracked character is in any more is removed; the others stay.
        await FleetWriter.WriteFcInventoryAsync(conn, 9999999, new List<FleetWriter.InventoryItem>
        {
            new() { SortedContainer = 20000, SortedSlotIndex = 0, ItemId = 22500, Quantity = 1 },
            new() { SortedContainer = 20001, SortedSlotIndex = 3, ItemId = 22501, Quantity = 2 },
        });
        var removedSlots = await FleetWriter.RemoveOrphanFcChestsAsync(conn);
        chest = await Rows(conn, "SELECT fc_id, count(*) FROM companion_fc_inventory_snapshot GROUP BY fc_id ORDER BY fc_id");
        Check(removedSlots == 2 && Show(chest) == "9000001,1",
            $"the chest of an FC no character is in is removed ({removedSlots} slots), the chest of a current FC stays: " + Show(chest));
        Check(await FleetWriter.RemoveOrphanFcChestsAsync(conn) == 0, "running the chest clean-up again removes nothing");

        // Submarines, including the route bytes and a sub with no voyage.
        var subs = new List<FleetWriter.SubmarineRecord>
        {
            new() { SubName = "Submersible-1", Level = 120, Part1 = 24360, Part2 = 24361, Part3 = 24362, Part4 = 24363,
                Points = new byte[] { 12, 15, 0, 0, 0 }, ReturnTime = 1_790_000_000, CurrentExp = 123456, NextLevelExp = 999999 },
            new() { SubName = "Submersible-2", Level = 1, Part1 = 21792, Part2 = 21793, Part3 = 21794, Part4 = 21795 },
        };
        await FleetWriter.WriteSubmarinesAsync(conn, owner, subs);
        var subRows = await Rows(conn, "SELECT sub_name, level, part1, part4, points, return_time, current_exp, next_level_exp FROM companion_submarine_snapshot ORDER BY sub_name");
        Check(Show(subRows) == "Submersible-1,120,24360,24363,0x0C0F000000,1790000000,123456,999999 / Submersible-2,1,21792,21795,0x,null,0,0",
            "submarines are written in one statement, with route bytes and no return time when not on a voyage: " + Show(subRows));
        await FleetWriter.WriteSubmarinesAsync(conn, owner, subs.Take(1).ToList());
        subRows = await Rows(conn, "SELECT sub_name FROM companion_submarine_snapshot");
        Check(Show(subRows) == "Submersible-1", "writing again replaces that character's subs: " + Show(subRows));

        // Which subs are written, and their slots (from AutoRetainer's list order).
        Check(string.Join(",", FleetWriter.PlanSubmarines(new[] { "Orca", "Submersible-2", "New One" }, new[] { "Submersible-2", "Orca", "Submersible-1" })
                  .Select(p => $"{p.Name}:{p.Slot}")) == "Orca:1,Submersible-2:2",
            "subs are written in list order with their slot; build data under an old name (renamed) is left out; a sub with no build yet is skipped");
        Check(string.Join(",", FleetWriter.PlanSubmarines(Array.Empty<string>(), new[] { "Submersible-3", "Orca" })
                  .Select(p => $"{p.Name}:{p.Slot?.ToString() ?? "?"}")) == "Orca:?,Submersible-3:3",
            "with no list yet, every sub with build data is written, the slot from a default name or unknown");

        // sql/006 adds the slot column; existing default-named rows get their slot from the name.
        await Exec(conn, @"CREATE TABLE sub_craft_toggle (cid numeric NOT NULL, sub_name text NOT NULL,
            craft_enabled boolean NOT NULL DEFAULT false, updated_at timestamp NOT NULL DEFAULT now(), PRIMARY KEY (cid, sub_name))");
        await RunScript(conn, "sql/006_submarine_slot.sql");
        await RunScript(conn, "sql/006_submarine_slot.sql");
        subRows = await Rows(conn, "SELECT sub_name, slot FROM companion_submarine_snapshot");
        Check(Show(subRows) == "Submersible-1,1", "006 adds the slot column and fills it from the default name (running it twice is fine): " + Show(subRows));

        // A rename: slot 1 changes from Submersible-1 to Orca. Its Craft? setting follows; slot 2's stays.
        await Exec(conn, $@"INSERT INTO sub_craft_toggle (cid, sub_name, craft_enabled) VALUES
            ({owner}, 'Submersible-1', true), ({owner}, 'Submersible-2', true)");
        await FleetWriter.WriteSubmarinesAsync(conn, owner, new List<FleetWriter.SubmarineRecord>
        {
            new() { SubName = "Submersible-1", Level = 50, Slot = 1 },
            new() { SubName = "Submersible-2", Level = 40, Slot = 2 },
        });
        await FleetWriter.WriteSubmarinesAsync(conn, owner, new List<FleetWriter.SubmarineRecord>
        {
            new() { SubName = "Orca", Level = 50, Slot = 1 },
            new() { SubName = "Submersible-2", Level = 41, Slot = 2 },
        });
        subRows = await Rows(conn, "SELECT sub_name, slot, level FROM companion_submarine_snapshot ORDER BY slot");
        var toggles = await Rows(conn, "SELECT sub_name, craft_enabled FROM sub_craft_toggle ORDER BY sub_name");
        Check(Show(subRows) == "Orca,1,50 / Submersible-2,2,41" && Show(toggles) == "Orca,True / Submersible-2,True",
            $"a renamed sub keeps its slot and its Craft? setting: {Show(subRows)} | {Show(toggles)}");

        // Two subs swapping names keep their own settings (neither old name is gone).
        await Exec(conn, $"UPDATE sub_craft_toggle SET craft_enabled = false WHERE cid = {owner} AND sub_name = 'Orca'");
        await FleetWriter.WriteSubmarinesAsync(conn, owner, new List<FleetWriter.SubmarineRecord>
        {
            new() { SubName = "Submersible-2", Level = 50, Slot = 1 },
            new() { SubName = "Orca", Level = 41, Slot = 2 },
        });
        toggles = await Rows(conn, "SELECT sub_name, craft_enabled FROM sub_craft_toggle ORDER BY sub_name");
        Check(Show(toggles) == "Orca,False / Submersible-2,True", "when two subs swap names, no setting is moved: " + Show(toggles));

        // Retainers: inserted, then updated in place.
        var retainers = new List<FleetWriter.RetainerRecord>
        {
            new() { RetainerId = 33777097243660301, Name = "Ret A", Job = 16, Gil = 1000, HasVenture = true, VentureId = 395,
                VentureBeginsAt = 1_789_990_000, VentureEndsAt = 1_790_000_000, Level = 100, HireOrderIndex = 0 },
            new() { RetainerId = 33777097243660302, Name = "Ret B", Job = 17, Level = 90, HireOrderIndex = null },
        };
        await FleetWriter.WriteRetainersAsync(conn, owner, retainers);
        retainers[0].Gil = 2500;
        retainers[0].Name = "Ret A2";
        await FleetWriter.WriteRetainersAsync(conn, owner, retainers);
        var ret = await Rows(conn, "SELECT retainer_id, owner_cid, name, job, gil, has_venture, venture_id, venture_ends_at, level, hire_order_index FROM companion_retainer_lookup ORDER BY retainer_id");
        Check(Show(ret) == $"33777097243660301,{owner},Ret A2,16,2500,True,395,1790000000,100,0 / 33777097243660302,{owner},Ret B,17,0,False,0,0,90,null",
            "retainers are written in one statement and updated in place; no hire order is stored as empty: " + Show(ret));

        // A dismissed retainer is removed; another character's retainers are not touched; an empty
        // list (missing data) removes nothing.
        await FleetWriter.WriteRetainersAsync(conn, 18014498578000002, new List<FleetWriter.RetainerRecord>
        {
            new() { RetainerId = 33777097243660399, Name = "Other Owner's", Job = 18, Level = 50 },
        });
        await FleetWriter.WriteRetainersAsync(conn, owner, retainers.Skip(1).ToList());
        ret = await Rows(conn, "SELECT retainer_id, owner_cid FROM companion_retainer_lookup ORDER BY retainer_id");
        Check(Show(ret) == $"33777097243660302,{owner} / 33777097243660399,18014498578000002",
            "a retainer no longer in the list is removed, another character's retainer stays: " + Show(ret));
        await FleetWriter.WriteRetainersAsync(conn, owner, new List<FleetWriter.RetainerRecord>());
        ret = await Rows(conn, "SELECT count(*) FROM companion_retainer_lookup WHERE owner_cid = " + owner);
        Check(Show(ret) == "1", "an empty retainer list removes nothing: " + Show(ret));

        // Housing upsert; the FC rank column is text in the real database.
        var housing = new FCTrackerConnector.HousingInfo
        {
            FcId = fc, FcName = "Panda Co", FcPoints = 123456, FcRank = 8, TotalMembers = 3, HasHouse = true,
            HouseCity = 2, HouseWard = 14, HousePlot = 30, FcMaster = "Aki", FcHomeWorldId = 54,
            FcFoundingDate = new DateTime(2024, 5, 1, 0, 0, 0, DateTimeKind.Utc),
        };
        await FleetWriter.WriteHousingAsync(conn, owner, housing);
        housing.FcPoints = 130000;
        housing.HasHouse = false; housing.HouseCity = null; housing.HouseWard = null; housing.HousePlot = null;
        await FleetWriter.WriteHousingAsync(conn, owner, housing);
        var house = await Rows(conn, "SELECT fc_id, fc_name, fc_points, fc_rank, has_house, house_ward, fc_master, fc_home_world_id FROM companion_character_housing");
        Check(Show(house) == "9000001,Panda Co,130000,8,False,null,Aki,54", "housing is inserted then updated, rank stored as text: " + Show(house));

        // A character that left its Free Company loses its row; another character's row stays.
        await FleetWriter.WriteHousingAsync(conn, 18014498578000002, housing);
        var removedHousing = await FleetWriter.RemoveHousingAsync(conn, new[] { owner });
        house = await Rows(conn, "SELECT cid FROM companion_character_housing");
        Check(removedHousing == 1 && Show(house) == "18014498578000002", "removing a character's FC details leaves the others: " + Show(house));

        // FCTracker's file: characters it knows to be in no FC are reported; unknown ones are not.
        var fcTrackerFile = System.IO.Path.GetTempFileName();
        System.IO.File.WriteAllText(fcTrackerFile, @"{""GatheredData"": {
            ""CharByCID"": {
                ""1"": {""CID"": 1, ""FC"": 9000001},
                ""2"": {""CID"": 2, ""FC"": null},
                ""3"": {""CID"": 3, ""FC"": 0},
                ""4"": {""CID"": 4, ""FC"": 9000077},
                ""5"": {""CID"": 5, ""FC"": 9000002}},
            ""FCData"": {""9000001"": {""FCName"": ""Panda Co"", ""FCPoints"": 5, ""House"": null},
                ""9000002"": {""FCName"": ""Ember Haven"", ""House"": {""City"": 2, ""Ward"": 6, ""Plot"": 27}}}}}");
        var withoutFc = new HashSet<ulong>();
        var parsed = FCTrackerConnector.ReadHousingData(fcTrackerFile, withoutFc);
        System.IO.File.Delete(fcTrackerFile);
        Check(parsed.TryGetValue(5, out var withHouse) && withHouse.HasHouse && withHouse.HouseCity == 2 && withHouse.HouseWard == 7 && withHouse.HousePlot == 28,
            "FCTracker: the house address is the real one (FCTracker saves Ward 7 Plot 28 as 6 and 27)");
        parsed.Remove(5);
        Check(string.Join(",", parsed.Keys.OrderBy(k => k)) == "1" && !parsed[1].HasHouse
              && string.Join(",", withoutFc.OrderBy(k => k)) == "2,3",
            $"FCTracker: in an FC {string.Join(",", parsed.Keys)}, in none {string.Join(",", withoutFc.OrderBy(k => k))} (an FC it has no details for is neither)");

        // Fingerprints: same data in another order is the same; any change is different.
        var items = new List<FleetWriter.InventoryItem>
        {
            new() { RetainerId = owner, SortedContainer = 0, SortedSlotIndex = 0, ItemId = 22500, Quantity = 12 },
            new() { RetainerId = owner, SortedContainer = 1000, SortedSlotIndex = 3, ItemId = 40000, Quantity = 1, GearSetIds = new uint[] { 1, 4 } },
            new() { RetainerId = retA, SortedContainer = 10000, SortedSlotIndex = 7, ItemId = 21792, Quantity = 2 },
        };
        var shuffled = items.AsEnumerable().Reverse().ToList();
        Check(FleetWriter.Fingerprint(items) == FleetWriter.Fingerprint(shuffled), "the same items in a different order count as unchanged");
        var moreQty = items.Select(i => new FleetWriter.InventoryItem { RetainerId = i.RetainerId, SortedContainer = i.SortedContainer, SortedSlotIndex = i.SortedSlotIndex, ItemId = i.ItemId, Quantity = i.Quantity, GearSetIds = i.GearSetIds }).ToList();
        moreQty[0].Quantity++;
        Check(FleetWriter.Fingerprint(items) != FleetWriter.Fingerprint(moreQty), "a changed quantity counts as a change");
        var newGear = moreQty.Select(i => new FleetWriter.InventoryItem { RetainerId = i.RetainerId, SortedContainer = i.SortedContainer, SortedSlotIndex = i.SortedSlotIndex, ItemId = i.ItemId, Quantity = i.Quantity, GearSetIds = i.GearSetIds }).ToList();
        newGear[0].Quantity--;
        newGear[1].GearSetIds = new uint[] { 1, 5 };
        Check(FleetWriter.Fingerprint(items) != FleetWriter.Fingerprint(newGear), "an item moved to another gear set counts as a change");
        var laterReturn = subs.Select(s => new FleetWriter.SubmarineRecord { SubName = s.SubName, Level = s.Level, Points = s.Points, ReturnTime = s.ReturnTime + 60 }).ToList();
        Check(FleetWriter.Fingerprint(subs) != FleetWriter.Fingerprint(laterReturn), "a sub sent on a new voyage counts as a change");
        Check(FleetWriter.Fingerprint(housing) != FleetWriter.Fingerprint(new FCTrackerConnector.HousingInfo { FcId = fc, FcName = "Panda Co" }),
            "changed housing details count as a change");

        // Change tracker: skips repeats, forgets failures, and forgets everything after the refresh interval.
        var start = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc);
        var tracker = new ChangeTracker(TimeSpan.FromHours(1), start);
        tracker.BeginSync(start);
        Check(!tracker.IsUnchanged("inv:1", "A"), "nothing is skipped the first time");
        tracker.Remember("inv:1", "A");
        tracker.BeginSync(start.AddMinutes(1));
        Check(tracker.IsUnchanged("inv:1", "A") && !tracker.IsUnchanged("inv:1", "B"), "the same data is skipped next sync, changed data is not");
        tracker.Forget("inv:1");
        Check(!tracker.IsUnchanged("inv:1", "A"), "after a failed write the data is written again next sync");
        tracker.Remember("inv:1", "A");
        tracker.BeginSync(start.AddMinutes(61));
        Check(!tracker.IsUnchanged("inv:1", "A"), "everything is written again once an hour");

        // Which characters are synced: every one except the unticked, or only the ticked.
        var chosen = new HashSet<ulong>();
        var skipped = new HashSet<ulong>();
        Check(CharacterChoice.ShouldSync(11, false, chosen, skipped), "by default every character is synced");
        CharacterChoice.Set(11, false, false, chosen, skipped);
        Check(!CharacterChoice.ShouldSync(11, false, chosen, skipped) && CharacterChoice.ShouldSync(12, false, chosen, skipped),
            "an unticked character is left out; others (and new ones) are still synced");
        Check(!CharacterChoice.ShouldSync(12, true, chosen, skipped), "with only ticked characters, a new character is left out");
        CharacterChoice.Set(13, true, true, chosen, skipped);
        Check(CharacterChoice.ShouldSync(13, true, chosen, skipped) && !CharacterChoice.ShouldSync(11, true, chosen, skipped),
            "with only ticked characters, the ticked one is synced");
        Check(!CharacterChoice.ShouldSync(11, false, chosen, skipped), "switching back keeps the earlier unticked list");
        CharacterChoice.SetExactly(new ulong[] { 11, 12, 13, 14 }, new HashSet<ulong> { 14 }, true, chosen, skipped);
        Check(new ulong[] { 11, 12, 13, 14 }.Where(c => CharacterChoice.ShouldSync(c, true, chosen, skipped)).SequenceEqual(new ulong[] { 14 }),
            "ticking exactly some characters (e.g. those with subs) unticks the rest");
        CharacterChoice.SetExactly(new ulong[] { 11, 12 }, new HashSet<ulong>(), false, chosen, skipped);
        Check(!CharacterChoice.ShouldSync(12, false, chosen, skipped) && CharacterChoice.ShouldSync(99, false, chosen, skipped),
            "untick all leaves out every listed character, but not one added later");

        // Sync schedule: every interval, and 5 seconds after a logout, but at most every 30 seconds.
        var five = TimeSpan.FromMinutes(5);
        var sched = new SyncSchedule();
        Check(sched.ShouldStart(start, five, busy: false) == "interval", "the first sync starts straight away");
        sched.Started(start);
        Check(sched.ShouldStart(start.AddMinutes(2), five, busy: false) == null, "no sync before the interval");
        sched.RequestSoon(start.AddMinutes(2));
        Check(sched.ShouldStart(start.AddMinutes(2).AddSeconds(3), five, busy: false) == null, "not in the first seconds after a logout");
        Check(sched.ShouldStart(start.AddMinutes(2).AddSeconds(5), five, busy: true) == null, "not while a sync is running");
        Check(sched.ShouldStart(start.AddMinutes(2).AddSeconds(6), five, busy: false) == "after logout", "a few seconds after a logout, once the last sync is done");
        sched.Started(start.AddMinutes(2).AddSeconds(6));
        Check(!sched.Requested, "that sync covers the logout");
        sched.RequestSoon(start.AddMinutes(2).AddSeconds(10));
        Check(sched.ShouldStart(start.AddMinutes(2).AddSeconds(20), five, busy: false) == null, "quick relogs wait for the 30-second gap");
        Check(sched.ShouldStart(start.AddMinutes(2).AddSeconds(36), five, busy: false) == "after logout", "then sync");
        sched.Started(start.AddMinutes(2).AddSeconds(36));
        Check(sched.ShouldStart(start.AddMinutes(7).AddSeconds(36), five, busy: false) == "interval", "the interval counts from the last sync");

        // sql/005 removes the old history table, but only once nothing has written it for 10 minutes.
        string? refused = null;
        try { await RunScript(conn, "sql/005_remove_character_snapshot.sql"); }
        catch (PostgresException ex) { refused = ex.MessageText; }
        var stillThere = Show(await Rows(conn, "SELECT to_regclass('companion_character_snapshot') IS NOT NULL"));
        Check(refused != null && refused.Contains("older than 0.4.0") && stillThere == "True",
            "005 refuses while the old table was written in the last 10 minutes, and changes nothing: " + refused);
        await Exec(conn, "UPDATE companion_character_snapshot SET snapshot_at = now() - interval '1 hour'");
        await RunScript(conn, "sql/005_remove_character_snapshot.sql");
        await RunScript(conn, "sql/005_remove_character_snapshot.sql");
        var gone = Show(await Rows(conn, "SELECT to_regclass('companion_character_snapshot') IS NULL, (SELECT count(*) FROM companion_character_current)"));
        Check(gone == "True,3", "005 removes the old table (running it again is harmless); the current rows stay: " + gone);
        // "Check what I can see": when things were stored (read only), and the report's wording.
        await Exec(conn, "UPDATE companion_inventory_snapshot SET updated_at = now() - interval '3 hours' WHERE retainer_id = 33777097243660301");
        var ages = await FleetWriter.ReadStoredAgesAsync(conn, new ulong[] { owner, 18014498578000099 }, new ulong[] { fc, 9999998 });
        Check(ages.Synced.ContainsKey(owner) && !ages.Synced.ContainsKey(18014498578000099)
              && ages.Inventory.ContainsKey((owner, owner)) && ages.Inventory[(owner, retA)].TotalHours >= 2.9 && ages.Inventory[(owner, retA)].TotalHours < 3.1
              && !ages.Inventory.ContainsKey((18014498578000002, 18014498578000002))
              && ages.Chest.ContainsKey(fc) && !ages.Chest.ContainsKey(9999998),
            $"stored ages: per character, per inventory source and per chest, only for the ones asked: retainer A {ages.Inventory[(owner, retA)].TotalHours:0.0}h");

        var facts = new CheckReport.Facts
        {
            Version = "0.9.0", At = new DateTime(2026, 10, 7, 12, 0, 0), AccountLabel = "acct1",
            AutoRetainerReady = true, Registered = 40, LeftOut = 38, AllaganToolsReady = true, FcTrackerPath = @"C:\fct.json", FcTrackerFound = true,
        };
        facts.Characters.Add(new CheckReport.Character
        {
            Cid = 1, Name = "Aki Main", World = "Maduin", Subs = 4, FcId = 9000001, FcName = "Panda Co", SyncedAgo = TimeSpan.FromMinutes(5),
            Bags = new() { Id = 1, Name = "Bags", Items = 143, StoredAgo = TimeSpan.FromMinutes(5) },
            Retainers = { new() { Id = 2, Name = "Oldret", Items = 80, StoredAgo = TimeSpan.FromHours(1) }, new() { Id = 3, Name = "Newret" } },
            Chest = new() { Id = 9000001, Name = "FC chest" },
            Housing = new FCTrackerConnector.HousingInfo { FcId = 9000001, HasHouse = true, HouseCity = 2, HouseWard = 7, HousePlot = 28 },
        });
        facts.Characters.Add(new CheckReport.Character
        {
            Cid = 4, Name = "Bex", World = "Maduin", Bags = new() { Id = 4, Name = "Bags", Items = 1, StoredAgo = TimeSpan.FromDays(3) }, SyncedAgo = TimeSpan.FromDays(3),
        });
        var report = CheckReport.ToText(facts);
        Check(report.Contains("AutoRetainer: running, 40 characters (38 left out in settings)") && report.Contains("2 things to fix (marked !)."),
            "the report starts with the other plugins and how many things to fix:\n" + report);
        Check(report.Contains("  Retainer Oldret: 80 items, stored 1 hour ago") && report.Contains("  Retainer Newret: not seen, nothing stored")
              && report.Contains("  ! Open Newret at a summoning bell on this PC.") && report.Contains("  ! Open the Free Company chest of Panda Co on this PC.")
              && report.Contains("  House: Lavender Beds, Ward 7, Plot 28") && report.Contains("  Bags: 143 items, stored 5 minutes ago"),
            "per character: each part, when it was stored, the house, and what to open");
        Check(report.Contains("Bex @ Maduin\r\n  Last synced: 3 days ago".Replace("\r\n", Environment.NewLine)) && report.Contains("  Free Company: none")
              && CheckReport.ToFix(facts.Characters[1], facts).Count == 0, "a character with nothing to fix has no ! lines");

        facts.AllaganToolsReady = false;
        facts.DatabaseProblem = "no saved connection (Settings → Postgres).";
        report = CheckReport.ToText(facts);
        Check(report.Contains("! AllaganTools is not running") && report.Contains("Database: not checked, no saved connection")
              && !report.Contains("Last synced") && !report.Contains("Newret: not seen") && CheckReport.ToFixCount(facts) == 2,
            "without AllaganTools or the database, the report says so once instead of listing every part:\n" + report);
        // sql/007: when each retainer's items were last seen. Without the column nothing fails.
        await Exec(conn, $"INSERT INTO companion_retainer_lookup (retainer_id, owner_cid, name) VALUES (77001, {owner}, 'Empty Ret') ON CONFLICT DO NOTHING");
        Check(!await FleetWriter.MarkRetainersSeenAsync(conn, new ulong[] { 77001 }), "before sql/007, marking retainers seen reports the column is missing");
        await RunScript(conn, "sql/007_retainer_items_seen.sql");
        await RunScript(conn, "sql/007_retainer_items_seen.sql");
        Check(await FleetWriter.MarkRetainersSeenAsync(conn, new ulong[] { 77001 })
              && Show(await Rows(conn, "SELECT items_seen_at > now() - interval '1 minute' FROM companion_retainer_lookup WHERE retainer_id = 77001")) == "True"
              && Show(await Rows(conn, "SELECT count(*) FROM companion_retainer_lookup WHERE items_seen_at IS NOT NULL")) == "1",
            "after sql/007 (run twice), a seen retainer gets its time, even with no items; others are untouched");

        await DetailsChecks(conn);
        ReaderChecks();
    }

    // sql/008 and DetailsWriter: PCs, voyage loot, venture rewards and market listings.
    private static async Task DetailsChecks(NpgsqlConnection conn)
    {
        var pc = new DetailsWriter.PcStatus
        {
            PcName = "DESKTOP-1", AccountLabel = "Account 1", PluginVersion = "0.10.0", AutoRetainerReady = true, AllaganToolsReady = true,
            FcTrackerFound = true, SubmarineTrackerFound = false, VentureStatsFound = true, CharactersSynced = 1, CharactersLeftOut = 39,
            Problems = new() { "Open retainer Newret (Aki@Maduin) at a summoning bell." },
        };
        Check(!await DetailsWriter.WritePcStatusAsync(conn, pc) && await DetailsWriter.ReadNewestVoyagesAsync(conn, new ulong[] { 1 }) == null
              && !await DetailsWriter.WriteVenturesAsync(conn, new[] { new DetailsWriter.VentureRow { OwnerCid = 1, RetainerName = "R", At = 1, ItemId = 1 } }),
            "before sql/008, the new writes report their tables are missing and change nothing");
        await RunScript(conn, "sql/008_pcs_loot_ventures_listings.sql");
        await RunScript(conn, "sql/008_pcs_loot_ventures_listings.sql");

        Check(await DetailsWriter.WritePcStatusAsync(conn, pc), "after sql/008 (run twice), a PC's status is written");
        pc.CharactersSynced = 2;
        pc.Problems.Clear();
        await DetailsWriter.WritePcStatusAsync(conn, pc);
        Check(await DetailsWriter.WritePcCheckAsync(conn, "DESKTOP-1", "Account 1", "0.10.0", "Nothing to fix.", 0), "a check result is written");
        var pcRows = await Rows(conn, "SELECT pc_name, account_label, characters_synced, cardinality(problems), check_text, check_to_fix, last_sync_at IS NOT NULL FROM companion_pc_status");
        Check(Show(pcRows) == "DESKTOP-1,Account 1,2,0,Nothing to fix.,0,True",
            "one row per PC and account: the newest status, and the check kept beside it: " + Show(pcRows));

        var loot = new List<DetailsWriter.LootRow>
        {
            new() { FcId = 9000001, Register = 1700000000, Return = 1790000000, Sector = 15, SubName = "Sub-1", PrimaryItem = 22500, PrimaryCount = 3, AdditionalItem = 22505, AdditionalCount = 1, AdditionalHq = false, Valid = true },
            new() { FcId = 9000001, Register = 1700000000, Return = 1790000000, Sector = 22, SubName = "Sub-1", PrimaryItem = 22507, PrimaryCount = 1, Valid = true },
        };
        Check(await DetailsWriter.WriteVoyageLootAsync(conn, loot) && await DetailsWriter.WriteVoyageLootAsync(conn, loot), "voyage loot is written, and writing it again is harmless");
        var newestVoyage = await DetailsWriter.ReadNewestVoyagesAsync(conn, new ulong[] { 9000001, 9000002 });
        Check(Show(await Rows(conn, "SELECT count(*), sum(primary_count) FROM companion_voyage_loot")) == "2,4"
              && newestVoyage != null && newestVoyage.Count == 1 && newestVoyage[9000001] == 1790000000,
            "each sector is stored once, and the newest voyage per FC is known so only newer ones are read");

        // sql/009: the order a voyage ran its sectors in. Voyages stored before it count as not read yet,
        // so the next sync fills in their order; a stored order is not changed.
        await RunScript(conn, "sql/009_voyage_sector_order.sql");
        await RunScript(conn, "sql/009_voyage_sector_order.sql");
        var beforeLegs = await DetailsWriter.ReadNewestVoyagesAsync(conn, new ulong[] { 9000001 });
        loot[0].Leg = 2;
        loot[1].Leg = 1;
        Check(beforeLegs != null && beforeLegs.Count == 0 && await DetailsWriter.WriteVoyageLootAsync(conn, loot),
            "after sql/009 (run twice), voyages stored without their order are read again");
        loot[0].Leg = 1;
        loot[1].Leg = 2;
        await DetailsWriter.WriteVoyageLootAsync(conn, loot);
        var legRows = await Rows(conn, "SELECT sector, leg FROM companion_voyage_loot ORDER BY leg");
        var afterLegs = await DetailsWriter.ReadNewestVoyagesAsync(conn, new ulong[] { 9000001 });
        Check(Show(legRows) == "22,1 / 15,2" && afterLegs != null && afterLegs[9000001] == 1790000000,
            "their order is filled in once, and then they count as read: " + Show(legRows));

        var ventures = new List<DetailsWriter.VentureRow>
        {
            new() { OwnerCid = 18014498578000001, RetainerName = "Oldret", At = 1790000100, ItemId = 5111, Quantity = 120, VentureId = 395 },
            new() { OwnerCid = 18014498578000001, RetainerName = "Oldret", At = 1790003700, ItemId = 12345, Hq = true, Quantity = 1, VentureId = 395 },
        };
        Check(await DetailsWriter.WriteVenturesAsync(conn, ventures) && await DetailsWriter.WriteVenturesAsync(conn, ventures), "venture rewards are written, and again harmlessly");
        var newestVenture = await DetailsWriter.ReadNewestVenturesAsync(conn, new ulong[] { 18014498578000001 });
        Check(Show(await Rows(conn, "SELECT count(*), sum(quantity) FROM companion_venture_result")) == "2,121"
              && newestVenture != null && newestVenture[(18014498578000001, "Oldret")] == 1790003700,
            "each reward is stored once, with the newest per retainer known");

        // Listings: a listing still up keeps when it was first seen, even after the list closes up or a
        // price change; a new one starts now; a retainer not seen keeps its stored listings.
        var listed = new List<DetailsWriter.Listing>
        {
            new() { RetainerId = 501, Slot = 0, ItemId = 22500, Quantity = 10, UnitPrice = 9000 },
            new() { RetainerId = 501, Slot = 1, ItemId = 44000, Quantity = 1, Hq = true, UnitPrice = 250000 },
            new() { RetainerId = 502, Slot = 0, ItemId = 5111, Quantity = 99, UnitPrice = 50 },
        };
        Check(await DetailsWriter.WriteListingsAsync(conn, 18014498578000001, new ulong[] { 501, 502 }, listed), "market listings are written");
        await Exec(conn, "UPDATE companion_market_listing SET first_seen_at = now() - interval '10 days'");
        var later = new List<DetailsWriter.Listing>
        {
            new() { RetainerId = 501, Slot = 0, ItemId = 44000, Quantity = 1, Hq = true, UnitPrice = 199000 },
            new() { RetainerId = 501, Slot = 1, ItemId = 30000, Quantity = 5, UnitPrice = 100 },
        };
        await DetailsWriter.WriteListingsAsync(conn, 18014498578000001, new ulong[] { 501 }, later);
        var listingRows = await Rows(conn, "SELECT retainer_id, slot, item_id, unit_price, first_seen_at < now() - interval '9 days' FROM companion_market_listing ORDER BY retainer_id, slot");
        Check(Show(listingRows) == "501,0,44000,199000,True / 501,1,30000,100,False / 502,0,5111,50,True",
            "a repriced listing keeps its first-seen time after the list closed up, a sold one goes, a new one starts now: " + Show(listingRows));
        var carried = DetailsWriter.CarryFirstSeen(
            new List<DetailsWriter.Listing>
            {
                new() { RetainerId = 1, Slot = 0, ItemId = 7, Quantity = 1, FirstSeenAt = new DateTime(2026, 1, 1) },
                new() { RetainerId = 1, Slot = 1, ItemId = 7, Quantity = 1, FirstSeenAt = new DateTime(2026, 2, 1) },
            },
            new List<DetailsWriter.Listing> { new() { RetainerId = 1, Slot = 0, ItemId = 7, Quantity = 1 }, new() { RetainerId = 1, Slot = 1, ItemId = 7, Quantity = 2 } });
        Check(carried[0].FirstSeenAt == new DateTime(2026, 1, 1) && carried[1].FirstSeenAt == null,
            "two identical listings, one sold: the one left keeps the older time; a different quantity is a new listing");
    }

    // SubmarineTracker's file and AutoRetainer's statistics files, read as the plugin reads them.
    private static void ReaderChecks()
    {
        Check(SubmarineTrackerReader.DecodeId(new byte[] { 0xcf, 0, 0x40, 0, 0, 0, 0x89, 0x54, 0x40 }) == 0x0040000000895440UL
              && SubmarineTrackerReader.DecodeId(new byte[] { 0x05 }) == 5 && SubmarineTrackerReader.DecodeId(new byte[] { 0xcd, 1, 0 }) == 256
              && SubmarineTrackerReader.DecodeId(new byte[] { 0xa1, 0x41 }) == null && SubmarineTrackerReader.DecodeId(null) == null,
            "SubmarineTracker's Free Company ids are decoded from MessagePack");

        var dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fleet-reader-" + Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(dir);
        try
        {
            var db = System.IO.Path.Combine(dir, SubmarineTrackerReader.FileName);
            using (var sqlite = new System.Data.SQLite.SQLiteConnection($"Data Source={db}"))
            {
                sqlite.Open();
                using var cmd = sqlite.CreateCommand();
                cmd.CommandText = @"
                    CREATE TABLE submarine (FreeCompanyId BLOB NOT NULL, SubmarineId INTEGER NOT NULL, Name TEXT NOT NULL);
                    CREATE TABLE loot (FreeCompanyId BLOB NOT NULL, SubmarineId INTEGER NOT NULL, Return INTEGER NOT NULL, Sector INTEGER NOT NULL,
                        PrimaryItem INTEGER NOT NULL, PrimaryCount INTEGER NOT NULL, PrimaryHQ BOOLEAN NOT NULL, AdditionalItem INTEGER NOT NULL,
                        AdditionalCount INTEGER NOT NULL, AdditionalHQ BOOLEAN NOT NULL, Date INTEGER NOT NULL, Valid BOOLEAN NOT NULL);
                    INSERT INTO submarine VALUES (x'ce00895440', 1700000000, 'Sub-1');
                    INSERT INTO loot VALUES (x'ce00895440', 1700000000, 1790000000, 15, 22500, 3, 0, 22505, 1, 0, 1790000000, 1);
                    INSERT INTO loot VALUES (x'ce00895440', 1700000000, 1790090000, 15, 22500, 2, 0, 0, 0, 0, 1790090000, 1);
                    INSERT INTO loot VALUES (x'ce00895440', 1700000000, 1790090000, 4, 22501, 1, 0, 0, 0, 0, 1790090000, 1);
                    INSERT INTO loot VALUES (x'ce00895441', 1700000001, 1790090000, 3, 22501, 1, 0, 0, 0, 0, 1790090000, 0);";
                cmd.ExecuteNonQuery();
            }
            var all = SubmarineTrackerReader.ReadLoot(db, new Dictionary<ulong, long>(), new ulong[] { 0x895440 });
            var newer = SubmarineTrackerReader.ReadLoot(db, new Dictionary<ulong, long> { [0x895440] = 1790000000 }, new ulong[] { 0x895440, 0x895441 });
            Check(all.Count == 3 && all[0].SubName == "Sub-1" && all[0].AdditionalItem == 22505 && all[0].PrimaryCount == 3
                  && newer.Count == 3 && newer.All(r => r.Return == 1790090000) && newer.Any(r => r.FcId == 0x895441 && !r.Valid && r.SubName == null),
                "SubmarineTracker's loot is read per FC asked for, only newer than what is stored, with the sub's name");
            Check(string.Join(" ", all.Select(r => $"{r.Sector}:{r.Leg}")) == "15:1 15:1 4:2" && newer.First(r => r.FcId == 0x895441).Leg == 1,
                "each sector's leg is the order SubmarineTracker added it in (the order it was visited), counted per voyage");

            var statFile = System.IO.Path.Combine(dir, "0040000000ABCDEF_Oldret.statistic.json");
            System.IO.File.WriteAllText(statFile, @"{""Records"":[{""I"":5111,""T"":1790000100,""A"":120,""V"":395},{""I"":12345,""H"":1,""T"":1790003700},{""I"":0,""T"":1790003800}],""PlayerName"":""Aki@Maduin"",""RetainerName"":""Oldret""}");
            var parsed = VentureStatsReader.ParseFileName("0040000000ABCDEF_Oldret.statistic.json");
            var rewards = VentureStatsReader.ReadFile(statFile, 0x0040000000ABCDEF, "Oldret", 1790000100);
            Check(parsed is { } p && p.Cid == 0x0040000000ABCDEF && p.Retainer == "Oldret"
                  && VentureStatsReader.ParseFileName("DefaultConfig.json") == null && VentureStatsReader.ParseFileName("123_Short.statistic.json") == null
                  && rewards.Count == 1 && rewards[0].ItemId == 12345 && rewards[0].Hq && rewards[0].Quantity == 1 && rewards[0].VentureId == 0,
                "AutoRetainer's statistics: file names give the character and retainer, records newer than stored are read with its defaults");
        }
        finally
        {
            System.IO.Directory.Delete(dir, true);
        }
    }
}
