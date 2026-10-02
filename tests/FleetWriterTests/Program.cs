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
        // Character snapshots: one statement for every character.
        var written = await FleetWriter.WriteCharacterSnapshotsAsync(conn, new[]
        {
            new FleetWriter.CharacterSnapshot { Cid = 18014498578000001, Name = "Aki", World = "Maduin", RetainerCount = 10, SubmarineCount = 4,
                Gil = 4_000_000_000, Ceruleum = 5000, RepairKits = 300, AccountLabel = "Main", FcId = 9000001, NumSubSlots = 4 },
            new FleetWriter.CharacterSnapshot { Cid = 18014498578000002, Name = "Bex", World = "Behemoth", AccountLabel = "", FcId = 0 },
        });
        var snaps = await Rows(conn, "SELECT cid, name, world, retainer_count, submarine_count, gil, ceruleum, repair_kits, account_label, fc_id, num_sub_slots FROM companion_character_snapshot ORDER BY cid");
        Check(written == 2 && Show(snaps) == "18014498578000001,Aki,Maduin,10,4,4000000000,5000,300,Main,9000001,4 / 18014498578000002,Bex,Behemoth,0,0,0,0,0,null,null,0",
            "character snapshots are written in one statement; gil above 2 billion fits; an empty account label and FC 0 are stored as empty (null): " + Show(snaps));

        // Inventory: replaced as a whole, gear sets kept as int arrays.
        const ulong owner = 18014498578000001;
        var items = new List<FleetWriter.InventoryItem>
        {
            new() { RetainerId = owner, SortedContainer = 0, SortedSlotIndex = 0, ItemId = 22500, Quantity = 12 },
            new() { RetainerId = owner, SortedContainer = 1000, SortedSlotIndex = 3, ItemId = 40000, Quantity = 1, GearSetIds = new uint[] { 1, 4 } },
            new() { RetainerId = 33777097243660301, SortedContainer = 10000, SortedSlotIndex = 7, ItemId = 21792, Quantity = 2 },
        };
        await FleetWriter.WriteInventoryAsync(conn, owner, items);
        var inv = await Rows(conn, "SELECT retainer_id, sorted_container, sorted_slot_index, item_id, quantity, gear_set_ids FROM companion_inventory_snapshot ORDER BY sorted_container");
        Check(Show(inv) == $"{owner},0,0,22500,12,null / {owner},1000,3,40000,1,{{1,4}} / 33777097243660301,10000,7,21792,2,null",
            "inventory rows are written in one statement, with gear sets as arrays and none as empty: " + Show(inv));

        await FleetWriter.WriteInventoryAsync(conn, owner, items.Take(1).ToList());
        inv = await Rows(conn, "SELECT item_id, quantity FROM companion_inventory_snapshot");
        Check(Show(inv) == "22500,12", "writing again replaces the whole inventory (removed items disappear): " + Show(inv));

        await FleetWriter.WriteInventoryAsync(conn, 18014498578000002, items.Take(2).Select(i => new FleetWriter.InventoryItem
            { RetainerId = 18014498578000002, SortedContainer = i.SortedContainer, SortedSlotIndex = i.SortedSlotIndex, ItemId = i.ItemId, Quantity = i.Quantity }).ToList());
        var counts = await Rows(conn, "SELECT owner_cid, count(*) FROM companion_inventory_snapshot GROUP BY 1 ORDER BY 1");
        Check(Show(counts) == $"{owner},1 / 18014498578000002,2", "another character's inventory does not touch the first one: " + Show(counts));

        await FleetWriter.WriteInventoryAsync(conn, owner, new List<FleetWriter.InventoryItem>());
        counts = await Rows(conn, $"SELECT count(*) FROM companion_inventory_snapshot WHERE owner_cid = {owner}");
        Check(Show(counts) == "0", "an empty list clears that character's inventory");

        // A failed write leaves the old inventory in place (all or nothing).
        await FleetWriter.WriteInventoryAsync(conn, owner, items);
        var duplicate = new List<FleetWriter.InventoryItem> { items[0], items[0] };
        var failed = false;
        try { await FleetWriter.WriteInventoryAsync(conn, owner, duplicate); } catch (PostgresException) { failed = true; }
        counts = await Rows(conn, $"SELECT count(*) FROM companion_inventory_snapshot WHERE owner_cid = {owner}");
        Check(failed && Show(counts) == "3", "a write that fails (two items in the same slot) changes nothing: " + Show(counts));

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

        // Fingerprints: same data in another order is the same; any change is different.
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
    }
}
