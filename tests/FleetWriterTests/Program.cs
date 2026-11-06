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

    // Batch 4: the Parts page read through the database, as in the game: the first made-up fleet's view rows put in
    // stand-ins for the fleet_parts_* views, with the PostgreSQL types the app's views give (ids numeric, booleans), and
    // the answers the app's.
    private static async Task PartsReadFromTheDatabase(NpgsqlConnection conn)
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "parts-fixture.json");
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var first = doc.RootElement.GetProperty("cases")[0];
        var tables = new (string View, string Key, string Columns)[]
        {
            ("fleet_parts_characters_view", "characters", "cid numeric, name text, world text, account_label text, sort_index int, fc_id text, num_sub_slots int, has_house boolean"),
            ("fleet_parts_subs_view", "subs", "cid numeric, sub_name text, slot int, rank int, part1 int, part2 int, part3 int, part4 int, craft_enabled boolean"),
            ("fleet_parts_rules_view", "rules", "id int, rank int, cid_override numeric, sub_filter text, hull text, stern text, bow text, bridge text"),
            ("fleet_parts_fight_club_view", "fightClub", "id int, route text, hours numeric, repair_times text, gil_sub_day numeric, rec_build_notes text, repair_kit_cost numeric"),
            ("fleet_parts_spares_view", "spares", "cid numeric, fc_id text, is_retainer boolean, retainer_id numeric, retainer_name text, sorted_container int, sorted_slot_index int, item_id int, quantity int"),
        };
        foreach (var (view, key, columns) in tables)
        {
            await Exec(conn, $"DROP TABLE IF EXISTS {view}; CREATE TABLE {view} ({columns})");
            var names = columns.Split(", ").Select(c => c.Split(' ')[0]).ToList();
            foreach (var row in first.GetProperty("inputs").GetProperty(key).EnumerateArray())
            {
                await using var cmd = new NpgsqlCommand($"INSERT INTO {view} ({string.Join(", ", names)}) VALUES ({string.Join(", ", names.Select((n, i) => $"CAST(@p{i} AS {columns.Split(", ")[i].Split(' ')[1]})"))})", conn);
                for (var i = 0; i < names.Count; i++)
                {
                    var v = row.GetProperty(names[i]);
                    cmd.Parameters.AddWithValue($"p{i}", v.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.Null => DBNull.Value,
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        System.Text.Json.JsonValueKind.String => v.GetString()!,
                        _ => v.GetRawText(),
                    });
                }
                await cmd.ExecuteNonQueryAsync();
            }
        }
        var read = await FleetReader.ReadParts(conn);
        var expected = first.GetProperty("expected").EnumerateArray().Select(e => $"{e.GetProperty("sub_name").GetString()} {e.GetProperty("slot").GetString()} {e.GetProperty("needed_class").GetString()} {(e.GetProperty("spare_location").ValueKind == System.Text.Json.JsonValueKind.Null ? "craft" : e.GetProperty("spare_location").GetString())}").ToList();
        var mine = read.Needs.Select(n => $"{n.SubName} {n.Slot} {n.NeededClass} {n.SpareLocation ?? "craft"}").ToList();
        Check(mine.SequenceEqual(expected) && read.Needs.Count > 0 && read.Needs[0].Character == "Aki",
            $"batch 4: the Parts page reads the database's views and gets the app's answers ({mine.Count} parts)");
        await Exec(conn, string.Join("; ", tables.Select(t => $"DROP TABLE {t.View}")));
    }

    // Batch 4: the Parts page's working out (PartsPlanner) against the app's own answers for the same made-up fleets
    // (written by the app's tests/db/parts-fixture.js from its fleet_parts_* views and computePartsNeeded).
    private static void PartsAgainstTheApp()
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "parts-fixture.json");
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
        static string? S(System.Text.Json.JsonElement e, string k) => e.GetProperty(k).ValueKind == System.Text.Json.JsonValueKind.Null ? null
            : e.GetProperty(k).ValueKind == System.Text.Json.JsonValueKind.String ? e.GetProperty(k).GetString() : e.GetProperty(k).GetRawText();
        static long L(System.Text.Json.JsonElement e, string k) => long.Parse(S(e, k)!, System.Globalization.CultureInfo.InvariantCulture);
        static long? LN(System.Text.Json.JsonElement e, string k) => S(e, k) is { } t ? long.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
        static int? IN(System.Text.Json.JsonElement e, string k) => S(e, k) is { } t ? (int)double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
        static double? DN(System.Text.Json.JsonElement e, string k) => S(e, k) is { } t ? double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
        static bool B(System.Text.Json.JsonElement e, string k) => e.GetProperty(k).ValueKind == System.Text.Json.JsonValueKind.True;
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var i = c.GetProperty("inputs");
            var input = new PartsInputs(
                i.GetProperty("characters").EnumerateArray().Select(e => new PartsCharacter(L(e, "cid"), S(e, "name") ?? "", S(e, "world") ?? "", S(e, "account_label") ?? "",
                    IN(e, "sort_index"), S(e, "fc_id"), IN(e, "num_sub_slots") ?? 0, B(e, "has_house"))).ToList(),
                i.GetProperty("subs").EnumerateArray().Select(e => new PartsSub(L(e, "cid"), S(e, "sub_name") ?? "", IN(e, "slot"), IN(e, "rank") ?? 0,
                    IN(e, "part1"), IN(e, "part2"), IN(e, "part3"), IN(e, "part4"), B(e, "craft_enabled"))).ToList(),
                i.GetProperty("rules").EnumerateArray().Select(e => new PartsRule(L(e, "id"), IN(e, "rank") ?? 0, S(e, "cid_override"), S(e, "sub_filter"),
                    S(e, "hull"), S(e, "stern"), S(e, "bow"), S(e, "bridge"))).ToList(),
                i.GetProperty("fightClub").EnumerateArray().Select(e => new PartsFightClubRoute(L(e, "id"), S(e, "route") ?? "", DN(e, "hours"), S(e, "repair_times"),
                    DN(e, "gil_sub_day"), S(e, "rec_build_notes"), DN(e, "repair_kit_cost") ?? 0)).ToList(),
                i.GetProperty("spares").EnumerateArray().Select(e => new PartsSpare(LN(e, "cid"), S(e, "fc_id"), B(e, "is_retainer"), LN(e, "retainer_id"), S(e, "retainer_name"),
                    IN(e, "sorted_container") ?? 0, IN(e, "sorted_slot_index") ?? 0, IN(e, "item_id") ?? 0, IN(e, "quantity") ?? 0)).ToList());
            var mine = PartsPlanner.Plan(input).Select(n => string.Join(" | ", n.Cid, n.Character, n.World, n.Account, n.SortIndex, n.SubName, n.Rank, n.Slot,
                n.NeededClass, n.NeededModified, n.SpareLocation, n.RecycledFrom)).ToList();
            var app = c.GetProperty("expected").EnumerateArray().Select(e => string.Join(" | ", L(e, "cid"), S(e, "character_name") ?? "", S(e, "world") ?? "",
                S(e, "account_label") ?? "", IN(e, "sort_index"), S(e, "sub_name"), IN(e, "rank"), S(e, "slot"), S(e, "needed_class"), B(e, "needed_modified"),
                S(e, "spare_location"), S(e, "recycled_from"))).ToList();
            var first = Enumerable.Range(0, Math.Max(mine.Count, app.Count)).FirstOrDefault(k => k >= mine.Count || k >= app.Count || mine[k] != app[k], -1);
            if (c.GetProperty("name").GetString()!.StartsWith("rules by rank", StringComparison.Ordinal))
            {
                var plan = PartsPlanner.Plan(input);
                var ready = PartsPlanner.SubsReady(plan);
                var craft = PartsPlanner.ToCraft(plan);
                Check(ready.Count == 3 && ready.All(x => x.Sub.Rank > 0) && ready[0].Sub.Character == "Bex" && craft.Sum(x => x.Count) == plan.Count(n => n.ToCraft)
                      && craft.Select(x => x.Slot).Distinct().SequenceEqual(new[] { "Hull", "Stern", "Bow", "Bridge" }),
                    "batch 4: subs ready for new parts (built subs only, in Roster order) and the parts still to craft by slot: "
                    + string.Join(", ", ready.Select(x => $"{x.Sub.Character} {x.Sub.SubName}")) + " / " + string.Join(", ", craft.Select(x => $"{x.Slot} {x.Needed} {x.Count}")));
            }
        Check(first < 0, $"batch 4: parts the same as the app's, {c.GetProperty("name").GetString()} ({app.Count} parts)"
                + (first < 0 ? "" : $": part {first + 1}: plugin [{(first < mine.Count ? mine[first] : "none")}], app [{(first < app.Count ? app[first] : "none")}]"));
        }
    }

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

    // A file in the repository (the old-database/ scripts), found by walking up from the test program.
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
            await SpeedTestChecks(conn, new NpgsqlConnectionStringBuilder(connectionString) { SearchPath = Schema }.ConnectionString);
            await FleetReaderChecks(conn);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA IF EXISTS {Schema} CASCADE", conn);
            await drop.ExecuteNonQueryAsync();
        }

        Console.WriteLine(failures == 0 ? $"ALL {passes} CHECKS PASSED" : $"{failures} CHECK(S) FAILED");
        return failures == 0 ? 0 : 1;
    }

    // 2.0 stage 3: the main window's tabs read the app's fleet views (db/views.sql in the app). Stand-ins with the same
    // columns and types are made here; the views' own numbers are checked in the app's tests.
    private static async Task FleetReaderChecks(NpgsqlConnection conn)
    {
        var missing = false;
        try { await FleetReader.ReadSubs(conn); }
        catch (PostgresException ex) { missing = ex.SqlState == "42P01"; }
        Check(missing, "fleet views: without them the read fails with 'does not exist' (42P01), which the window explains");

        await Exec(conn, @"
            CREATE VIEW fleet_subs_view AS SELECT * FROM (VALUES
              (1::numeric, 'Aki'::text, 'Maduin'::text, 'Main'::text, 1::numeric, 2::int, 'Back'::text, 2::int, 90::int, 'S+-U+-C-U'::text, 1700000000::bigint, 'returned'::text, 50::bigint, 200::bigint, now()::timestamp),
              (1::numeric, 'Aki', 'Maduin', 'Main', 1::numeric, 2::int, 'Port', 1::int, 10::int, 'S-S-S-S', 0::bigint, 'in_port', NULL::bigint, NULL::bigint, now()::timestamp))
              AS t(cid, character_name, world, account_label, fc_id, sort_index, sub_name, slot, rank, build, return_time, status, current_exp, next_level_exp, updated_at);
            CREATE VIEW fleet_supplies_view AS SELECT * FROM (VALUES
              (1::numeric, 'Aki'::text, 'Maduin'::text, NULL::text, 1::numeric, NULL::int, now(), 300::int, 100.5::float8, 2.98::float8, 1107::int,
               40::int, NULL::float8, NULL::float8, 0::int, 14::int, 14::int))
              AS t(cid, name, world, account_label, fc_id, sort_index, last_synced_at, ceruleum, ceruleum_per_day, ceruleum_days_left, ceruleum_restock,
                   repair_kits, repair_kits_per_day, repair_kits_days_left, repair_kits_restock, window_days, target_days);
            CREATE VIEW fleet_summary_view AS SELECT 4::int AS subs, 1::int AS subs_returned, 1::int AS subs_in_port, 2::int AS subs_out,
              1800000000::bigint AS next_return_time, 160::int AS retainers, 3::int AS ventures_done, 2::int AS retainers_idle,
              1::int AS low_ceruleum, 0::int AS low_repair_kits, 2.98::float8 AS soonest_runs_out_days;");
        var subs = await FleetReader.ReadSubs(conn);
        Check(subs.Count == 2 && subs[0].SubName == "Port" && subs[0].Slot == 1 && subs[0].NextLevelExp == 0
              && subs[1] == new SubRow("Aki", "Maduin", "Main", 2, "Back", 2, 90, "S+-U+-C-U", 1700000000, 50, 200),
            "fleet views: subs read in slot order, with nulls as 0: " + string.Join(" | ", subs));
        var supplies = await FleetReader.ReadSupplies(conn);
        var s = supplies.Single();
        Check(s.Character == "Aki" && s.Account == "" && s.SortIndex == int.MaxValue && s.Ceruleum == 300 && s.CeruleumPerDay == 100.5
              && s.CeruleumDaysLeft == 2.98 && s.CeruleumRestock == 1107 && s.RepairKitsPerDay == null && s.RepairKitsDaysLeft == null
              && s.WindowDays == 14 && s.LastSynced.HasValue,
            "fleet views: supplies read, not enough history kept as null: " + s);
        var summary = await FleetReader.ReadSummary(conn);
        Check(summary == new SummaryRow(160, 3, 2, 1, 0, 2.98), "fleet views: the summary read: " + summary);

        Check(FleetReader.SubStatus(0, 1000) == "In port" && FleetReader.SubStatus(999, 1000) == "Back" && FleetReader.SubStatus(1000, 1000) == "Back"
              && FleetReader.SubStatus(1000 + 59, 1000) == "0:59" && FleetReader.SubStatus(1000 + 3 * 3600 + 5 * 60 + 9, 1000) == "3:05:09"
              && FleetReader.SubStatus(1000 + 2 * 86400 + 4 * 3600, 1000) == "2d 4:00:00",
            "fleet views: sub status reads In port, Back, or the time until it's back");
        var now = new DateTime(2026, 11, 5, 12, 0, 0);
        Check(FleetReader.Ago(now.AddSeconds(-12), now) == "12s ago" && FleetReader.Ago(now.AddMinutes(-4), now) == "4m ago" && FleetReader.Ago(now.AddHours(-2), now) == "2h ago",
            "fleet views: how long ago it was read");

        // Stage 4: the info bar line and what needs attention.
        var alerts = new List<AlertRow>
        {
            new("danger", "house", "Cute Kittehs!", "house not entered for 33 days; demolished after 12 more"),
            new("warn", "low_ceruleum", "Aki (Maduin)", "50 ceruleum (warning below 100)"),
            new("warn", "low_repair_kits", "Aki (Maduin)", "5 repair kits (warning below 10)"),
            new("warn", "low_ceruleum", "Bex (Maduin)", "10 ceruleum (warning below 100)"),
            new("warn", "autoretainer", "DESKTOP-1 (A)", "AutoRetainer was not running at its last sync"),
        };
        Check(FleetReader.InfoBarText(0, new List<AlertRow>()) == "Fleet" && FleetReader.InfoBarText(0, null) == "Fleet"
              && FleetReader.InfoBarText(4, null) == "Fleet: 4 back"
              && FleetReader.InfoBarText(4, alerts) == "Fleet: 4 back · 2 low · 2 to check"
              && FleetReader.InfoBarText(4, alerts, 3) == "Fleet: 4 back · 3 need parts · 2 low · 2 to check"
              && FleetReader.InfoBarText(0, alerts.Take(2).ToList()) == "Fleet: 1 low · 1 to check",
            "info bar: Fleet alone when nothing needs you, else subs back, characters low (each once) and the rest: " + FleetReader.InfoBarText(4, alerts));
        Check(FleetReader.SubsBack(subs, 1700000001) == 1 && FleetReader.SubsBack(subs, 1699999999) == 0 && FleetReader.SubsBack(null, 1) == 0,
            "info bar: subs back counts only subs whose return time has passed (not those in port)");
        await Exec(conn, @"CREATE VIEW fleet_alerts_view AS SELECT * FROM (VALUES ('danger'::text, 'house'::text, 'Cute Kittehs!'::text, 'not entered'::text, 33))
            AS t(level, kind, subject, detail, weight)");
        var read = await FleetReader.ReadAlerts(conn);
        Check(read.Count == 1 && read[0] == new AlertRow("danger", "house", "Cute Kittehs!", "not entered"), "info bar: what needs attention is read: " + string.Join(" | ", read));
        await Exec(conn, "DROP VIEW fleet_alerts_view");

        // 2.x, the rest of the tabs: the Roster, Retainers and PCs.
        await Exec(conn, @"
            CREATE VIEW fleet_roster_view AS SELECT * FROM (VALUES
              (1::numeric, 'Aki'::text, 'Maduin'::text, 'A'::text, 2::int, 4000000000::bigint, 50::int, 5::int, 10::int, 4::int, now(),
               'Cute Kittehs!'::text, 52000::int, 'Mist W12 P30'::text, 'DESKTOP-1'::text, 'A'::text, now(), 4::int, NULL::int, true, true),
              (2::numeric, 'Bex', 'Maduin', NULL, NULL, 0, NULL, NULL, 0, 0, now(), NULL, NULL, NULL, NULL, NULL, NULL, 0, 125, false, false))
              AS t(cid, name, world, account_label, sort_index, gil, ceruleum, repair_kits, retainer_count, submarine_count, last_synced_at,
                   fc_name, fc_points, house, login_pc, login_label, logged_in_at, subs_built, behind_minutes, low_ceruleum, low_repair_kits);
            CREATE VIEW fleet_retainers_view AS SELECT * FROM (VALUES
              (10::numeric, 1::numeric, 'Aki'::text, 'Maduin'::text, 'A'::text, 2::int, 'Seen'::text, 18::int, 100::int, 5000::bigint, true, 395::int, 1800000000::bigint, 0::int, 'ilvl'::text, 690::int, true, true),
              (11::numeric, 99, NULL, NULL, NULL, NULL, 'Orphan', 0, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, false, false))
              AS t(retainer_id, owner_cid, owner_name, world, account_label, sort_index, retainer_name, job, level, gil, has_venture, venture_id, venture_ends_at,
                   hire_order_index, target_mode, target_value, only_crafted, items_seen);
            CREATE VIEW fleet_retainer_gear_view AS SELECT * FROM (VALUES (10::numeric, 40000::int), (10::numeric, 40001::int)) AS t(retainer_id, item_id);
            CREATE VIEW fleet_pcs_view AS SELECT * FROM (VALUES
              ('DESKTOP-1'::text, 'A'::text, '2.0.0.9'::text, now(), true, true, false, true, true, 12::int, 2::int, 3::int, E'Aki: no FC chest\nBex: no retainers'::text, 'All good'::text, 0::int, now()),
              ('LAPTOP', '', NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, '', NULL, NULL, NULL))
              AS t(pc_name, account_label, plugin_version, last_sync_at, autoretainer_ready, allagantools_ready, fctracker_found, submarinetracker_found,
                   venture_stats_found, characters_synced, characters_left_out, characters_elsewhere, problems, check_text, check_to_fix, check_at);");
        var roster = await FleetReader.ReadRoster(conn);
        Check(roster.Count == 2 && roster[0].Character == "Aki" && roster[0].Gil == 4000000000 && roster[0].House == "Mist W12 P30" && roster[0].FcPoints == 52000
              && roster[0].LowCeruleum && roster[0].BehindMinutes == null && roster[0].LoginPc == "DESKTOP-1"
              && roster[1].Account == "" && roster[1].Ceruleum == null && roster[1].FcName == null && roster[1].BehindMinutes == 125 && !roster[1].LowRepairKits,
            "rest of the tabs: the roster read, nulls kept as unknown: " + string.Join(" | ", roster));
        var retainers = await FleetReader.ReadRetainers(conn);
        var seen = retainers.Retainers[0];
        Check(retainers.Retainers.Count == 2 && seen.Name == "Seen" && seen.Job == 18 && seen.VentureId == 395 && seen.VentureEndsAt == 1800000000
              && seen.TargetMode == "ilvl" && seen.TargetValue == 690 && seen.OnlyCrafted && seen.ItemsSeen
              && retainers.Retainers[1].Owner == "" && retainers.Retainers[1].Gil == null && !retainers.Retainers[1].HasVenture && retainers.Retainers[1].Level == null
              && retainers.Gear.Count == 1 && retainers.Gear[10].SequenceEqual(new[] { 40000, 40001 }),
            "rest of the tabs: the retainers and what they wear read: " + string.Join(" | ", retainers.Retainers));
        var pcs = await FleetReader.ReadPcs(conn);
        Check(pcs.Count == 2 && pcs[0].Pc == "LAPTOP" && pcs[0].Problems.Count == 0 && pcs[0].AutoRetainer == null
              && pcs[1].Problems.SequenceEqual(new[] { "Aki: no FC chest", "Bex: no retainers" }) && pcs[1].FcTracker == false && pcs[1].Elsewhere == 3,
            "rest of the tabs: the PCs read, what each couldn't see one per line: " + string.Join(" | ", pcs.Select(p => $"{p.Pc} {p.Problems.Count}")));
        Check(FleetReader.VentureStatus(false, 0, 1000) == "Idle" && FleetReader.VentureStatus(true, 0, 1000) == "Idle"
              && FleetReader.VentureStatus(true, 1000, 1000) == "Venture Complete" && FleetReader.VentureStatus(true, 1000 + 3600, 1000) == "1:00:00",
            "rest of the tabs: a retainer's status reads as the app's Retainers tab (Idle, Venture Complete or the time left)");
        var levels = new Dictionary<int, int?> { [1] = 690, [2] = 700, [3] = null };
        Check(FleetReader.AverageItemLevel(new[] { 1, 2, 3 }, id => levels[id]) == 695 && FleetReader.AverageItemLevel(new[] { 3 }, id => levels[id]) == null
              && FleetReader.AverageItemLevel(null, id => 1) == null,
            "rest of the tabs: the average item level leaves out items the game doesn't know, as the app does");
        Check(FleetReader.SyncedBy("DESKTOP-1", "A") == "A on DESKTOP-1" && FleetReader.SyncedBy("LAPTOP", "") == "(no label) on LAPTOP" && FleetReader.SyncedBy(null, null) == "—",
            "rest of the tabs: Synced by reads as the app's Roster");
        Check(FleetReader.PcCharacters(12, 2, 3) == "12 (2 left out) (3 left to another setup)" && FleetReader.PcCharacters(5, 0, null) == "5" && FleetReader.PcCharacters(null, null, null) == "—",
            "rest of the tabs: a setup's characters read as the app's PCs tab");
        Check(FleetReader.CompareVersions("2.0.0.9", "2.0.0.10") < 0 && FleetReader.CompareVersions("2.0.0", "2.0.0.0") == 0 && FleetReader.CompareVersions(null, "0.1") < 0,
            "rest of the tabs: plugin versions compare part by part");
        await Exec(conn, "DROP VIEW fleet_roster_view, fleet_retainers_view, fleet_retainer_gear_view, fleet_pcs_view");

        // The app's orders: the Roster's (saved place, none last, then name) and the Retainers tab's (owner's place, owner,
        // hiring order, retainer); Group by account (A to Z, Ungrouped last).
        RosterRow R(string name, int sortIndex, string account = "") => new(0, name, "Maduin", account, sortIndex, null, null, null, 0, null, null, 0, 0, null, null, null, null, null, false, false);
        var rosterOrder = FleetOrder.Roster(new[] { R("Cal", int.MaxValue), R("Bex", 5), R("Aki", int.MaxValue), R("Dee", 1) });
        Check(string.Join(",", rosterOrder.Select(r => r.Character)) == "Dee,Bex,Aki,Cal", "orders: the Roster's (saved place, none last, then name): " + string.Join(",", rosterOrder.Select(r => r.Character)));
        RetainerRow T(string owner, int sortIndex, int hire, string name) => new(0, owner, "Maduin", "", sortIndex, name, 0, null, null, false, 0, 0, hire, null, null, false, true);
        var retainerOrder = FleetOrder.Retainers(new[] { T("Aki", 3, 1, "B"), T("Bex", 1, int.MaxValue, "Z"), T("Aki", 3, 0, "Y"), T("Bex", 1, 2, "A"), T("Cal", int.MaxValue, 0, "C"), T("Aki", 3, int.MaxValue, "A") });
        Check(string.Join(",", retainerOrder.Select(r => $"{r.Owner}:{r.Name}")) == "Bex:A,Bex:Z,Aki:Y,Aki:B,Aki:A,Cal:C",
            "orders: retainers by their owner's place in the Roster, then hiring order (none last), as the app: " + string.Join(",", retainerOrder.Select(r => $"{r.Owner}:{r.Name}")));
        var groups = FleetOrder.ByAccount(new[] { R("Aki", 1, "B"), R("Bex", 2, ""), R("Cal", 3, "A"), R("Dee", 4, "B") }, r => r.Account);
        Check(string.Join(" | ", groups.Select(g => $"{g.Label}: {string.Join(",", g.Rows.Select(r => r.Character))}")) == "A: Cal | B: Aki,Dee | Ungrouped: Bex",
            "orders: Group by account, A to Z, Ungrouped last, rows kept in order: " + string.Join(" | ", groups.Select(g => $"{g.Label}: {string.Join(",", g.Rows.Select(r => r.Character))}")));

        // Stacks (Where is it?) and what the app's characters/item-locations.js groupByItem makes of them, generated from it.
        var whereStacks = new List<HoldingRow>
        {
            new("character", 1, null, 0, 10155, false, 5, "Aki", "Maduin", "Aki", "A", 2, null, int.MaxValue, null, false, "Bags"),
            new("character", 1, null, 1, 10155, false, 7, "Aki", "Maduin", "Aki", "A", 2, null, int.MaxValue, null, false, "Bags"),
            new("character", 1, null, 4000, 10155, false, 30, "Aki", "Maduin", "Aki", "A", 2, null, int.MaxValue, null, false, "Saddlebag"),
            new("retainer", 1, null, 10000, 10155, false, 1, "Aki", "Maduin", "Aki", "A", 2, "Zed", 1, null, false, "Retainer Zed"),
            new("retainer", 1, null, 10000, 10155, false, 2, "Aki", "Maduin", "Aki", "A", 2, "Amy", 0, null, false, "Retainer Amy"),
            new("retainer", 1, null, 12002, 10155, false, 3, "Aki", "Maduin", "Aki", "A", 2, "Bo", int.MaxValue, null, false, "Retainer Bo (for sale)"),
            new("fc", 1, 11, 20000, 10155, false, 100, "Kittehs", "", "Aki", "A", 2, null, int.MaxValue, null, false, "FC chest tab 1"),
            new("character", 2, null, 2, 10155, false, 4, "Bex", "Maduin", "Bex", "B", 1, null, int.MaxValue, null, false, "Bags"),
            new("character", 4, null, 0, 10155, false, 9, "Cal", "Exodus", "Cal", "", int.MaxValue, null, int.MaxValue, null, false, "Bags"),
            new("character", 3, null, 0, 10155, false, 8, "Cal", "Behemoth", "Cal", "", int.MaxValue, null, int.MaxValue, null, false, "Bags"),
            new("character", 2, null, 2001, 5, false, 999, "Bex", "Maduin", "Bex", "B", 1, null, int.MaxValue, null, false, "Crystals"),
        };
        var whereExpected = new[]
        {
            "Air Shard: 999 = Bex|Maduin|Crystals|999",
            "Ceruleum Tank: 169 = Bex|Maduin|Bags|4 / Aki|Maduin|Saddlebag|30 / Aki|Maduin|Bags|12 / Aki|Maduin|Retainer Amy|2 / Aki|Maduin|Retainer Zed|1 / Aki|Maduin|Retainer Bo (for sale)|3 / Kittehs||FC chest tab 1|100 / Cal|Behemoth|Bags|8 / Cal|Exodus|Bags|9",
        };
        var names = new Dictionary<int, string> { [10155] = "Ceruleum Tank", [5] = "Air Shard" };
        var whereText = FleetOrder.WhereIsIt(whereStacks, id => names[id]).Select(i => $"{i.Name}: {i.Total} = {string.Join(" / ", i.Places.Select(p => $"{p.Who}|{p.World}|{p.Where}|{p.Quantity}"))}").ToArray();
        Check(whereText.SequenceEqual(whereExpected), "Where is it?: the same items, totals, places and order as the app: " + string.Join(" || ", whereText));
        Check(FleetOrder.WhereIsIt(new[] { whereStacks[0] with { Money = true }, whereStacks[1] with { Quantity = 0 } }, id => names[id]).Count == 0,
            "Where is it?: gil and empty stacks left out");
        Check(FleetOrder.CharacterLocation(whereStacks[0] with { ContainerName = "Inventory 1" }) == "Inventory 1" && FleetOrder.CharacterLocation(whereStacks[0]) == "Container 0"
              && FleetOrder.CharacterLocation(whereStacks[3]) == "Retainer: Zed" && FleetOrder.CharacterLocation(whereStacks[3] with { RetainerName = "" }) == "Retainer: Unknown",
            "Inventory: a stack's Location reads as the app's Characters inventory");

        // Batch 2: retainer counts, Free Companies, every stack, the fleet's totals and the items held.
        await Exec(conn, @"
            CREATE VIEW fleet_retainer_counts_view AS SELECT 160::int AS retainers, 65::int AS ventures_done, 94::int AS retainers_away, 1::int AS retainers_idle;
            CREATE VIEW fleet_fcs_view AS SELECT * FROM (VALUES
              (9232379236109516865::numeric, 'Kittehs'::text, 'Aki'::text, 52000::int, '8'::text, 3::int, true, 'Mist W12 P30'::text, 34::int, now(), 9::int, 'warn'::text, 36::int, NULL::timestamptz, NULL::text, 'A'::text, 3::int),
              (12::numeric, 'Young', NULL, NULL, NULL, NULL, false, NULL, NULL, NULL, NULL, NULL, NULL, now(), 'Eligible in 3 days', NULL, NULL))
              AS t(fc_id, fc_name, fc_master, fc_points, fc_rank, total_members, has_house, house, home_world_id, house_last_visited, house_days, house_level,
                   demolition_in_days, eligible_at, eligibility, account_label, sort_index);
            CREATE VIEW fleet_holdings_view AS SELECT * FROM (VALUES
              ('character'::text, 1::numeric, 1::numeric, NULL::numeric, 0::int, 10155::int, true, 5::int, 'Aki'::text, 'Maduin'::text, 'Aki'::text, 'A'::text, 2::int, NULL::text, NULL::int, 'Inventory 1'::text, false, 'Bags'::text),
              ('fc', 1, NULL, 9232379236109516865, 22000, 1, false, 500000, 'Kittehs', NULL, 'Aki', 'A', 2, NULL, NULL, NULL, true, 'FC container 22000'))
              AS t(kind, owner_cid, retainer_id, fc_id, container, item_id, hq, quantity, who, world, owner_name, account_label, sort_index, retainer_name,
                   hire_order_index, container_name, money, place);
            CREATE VIEW fleet_item_totals_view AS SELECT 10155::int AS item_id, true AS hq, 5::bigint AS total;");
        var counts = await FleetReader.ReadRetainerCounts(conn);
        Check(counts == new RetainerCountsRow(160, 65, 94, 1), "batch 2: the retainer counts read: " + counts);
        var fcs = await FleetReader.ReadFcs(conn);
        Check(fcs.Count == 2 && fcs[0].FcId == 9232379236109516865m && fcs[0].Name == "Kittehs" && fcs[0].HasHouse && fcs[0].HouseLevel == "warn" && fcs[0].HomeWorld == 34 && fcs[0].Points == 52000
              && fcs[1].Name == "Young" && !fcs[1].HasHouse && fcs[1].Eligibility == "Eligible in 3 days" && fcs[1].SortIndex == int.MaxValue && fcs[1].Account == "",
            "batch 2: the Free Companies read, in the Roster's order: " + string.Join(" | ", fcs));
        var holdings = await FleetReader.ReadHoldings(conn, "owner_cid = ANY(CAST(@ids AS numeric[]))", p => p.AddWithValue("ids", new long[] { 1 }));
        Check(holdings.Count == 2 && holdings[0].Hq && holdings[0].Place == "Bags" && holdings[0].ContainerName == "Inventory 1" && holdings[1].Money && holdings[1].FcId == 9232379236109516865m && holdings[1].World == "",
            "batch 2: stacks read with a condition: " + string.Join(" | ", holdings));
        var none = await FleetReader.ReadHoldings(conn, "owner_cid = ANY(CAST(@ids AS numeric[]))", p => p.AddWithValue("ids", new long[] { 2 }));
        Check(none.Count == 0, "batch 2: only the stacks asked for");
        var totals = await FleetReader.ReadItemTotals(conn);
        Check(totals.Count == 1 && totals[0] == new ItemTotalRow(10155, true, 5), "batch 2: the fleet's totals read");
        var heldItems = await FleetReader.ReadHeldItems(conn);
        Check(heldItems.SequenceEqual(new[] { 10155 }), "batch 2: the items held, without gil: " + string.Join(",", heldItems));
        // Something the plugin can't take in one read (here a count too big for a number) fails that read alone, saying
        // why, and keeps what was read before; it doesn't stop the others.
        await Exec(conn, "DROP VIEW fleet_retainer_counts_view; CREATE VIEW fleet_retainer_counts_view AS SELECT 99999999999999999999::numeric AS retainers, 1 AS ventures_done, 1 AS retainers_away, 1 AS retainers_idle");
        var bad = await FleetReader.Read(conn, FleetReader.ReadRetainerCounts, new FleetRead<RetainerCountsRow>(counts, DateTime.Now, null));
        Check(bad.Data == counts && bad.Error != null && bad.Error.StartsWith("Could not read this from the database"), "batch 2: a read that can't be taken fails alone: " + bad.Error);
        var after = await FleetReader.Read(conn, FleetReader.ReadFcs, new FleetRead<List<FcRow>>(null, null, null));
        Check(after.Error == null && after.Data?.Count == 2, "batch 2: and the connection still reads the next one: " + after.Error);
        Check(holdings.All(h => !h.Equipped) && totals[0].Equipped == 0, "batch 2: views from before batch 3 (no equipped column) still read, nothing marked worn");
        await Exec(conn, "DROP VIEW fleet_retainer_counts_view, fleet_fcs_view, fleet_holdings_view, fleet_item_totals_view");

        // Batch 3: Salvage, Income, venture rewards, equipped gear, and searching word by word.
        await Exec(conn, @"
            CREATE VIEW fleet_holdings_view AS SELECT * FROM (VALUES
              ('character'::text, 1::numeric, 1::numeric, NULL::numeric, 1000::int, 40000::int, false, 1::int, 'Aki'::text, 'Maduin'::text, 'Aki'::text, 'A'::text, 2::int, NULL::text, NULL::int, 'GearSet0'::text, false, 'Equipped'::text, true),
              ('retainer', 1, 100, NULL, 3200, 40000, false, 2, 'Aki', 'Maduin', 'Aki', 'A', 2, 'Zed', 0, NULL, false, 'Armoury chest', false))
              AS t(kind, owner_cid, retainer_id, fc_id, container, item_id, hq, quantity, who, world, owner_name, account_label, sort_index, retainer_name,
                   hire_order_index, container_name, money, place, equipped);
            CREATE VIEW fleet_item_totals_view AS SELECT 40000::int AS item_id, false AS hq, 3::bigint AS total, 1::bigint AS equipped;
            CREATE VIEW fleet_salvage_view AS SELECT * FROM (VALUES
              (2::numeric, 'Bex'::text, 'Maduin'::text, 'B'::text, 1::int, 3::bigint, 1::bigint, 61000::bigint, 2::bigint, 0::bigint, 1::bigint, 0::bigint, 0::bigint, 0::bigint, 0::bigint, 1::bigint),
              (1, 'Aki', 'Maduin', 'A', NULL, 0, 2, 57000, 0, 0, 0, 0, 0, 2, 0, 0))
              AS t(cid, name, world, account_label, sort_index, standard, extravagant, gil, q22500, q22501, q22502, q22503, q22504, q22505, q22506, q22507);
            CREATE VIEW fleet_income_view AS SELECT * FROM (VALUES
              (1::numeric, 'Aki'::text, 'Maduin'::text, 'A'::text, 5::int, now(), 4::bigint, true, 3.5::numeric, 7::numeric, 2::numeric, 3::numeric, 61000::numeric, 4::numeric, 6920::numeric, 54080::numeric, 15451.4::float8, 17428.6::float8, 1977.1::float8, 3::int, 1730::numeric),
              (18014498577484004, 'Cal', 'Exodus', '', NULL, now(), 1, false, 0.4, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, NULL, 3, 1730))
              AS t(cid, name, world, account_label, sort_index, last_synced_at, subs, enough, span_days, pieces, from_chest, first_stored, salvage_gil, kits_used, kit_gil, net,
                   per_day, salvage_per_day, kit_per_day, window_days, kit_cost);
            CREATE VIEW fleet_venture_income_view AS SELECT * FROM (VALUES
              (1::numeric, 'Aki'::text, 'Maduin'::text, 'A'::text, 5::int, 5111::int, 10::bigint, NULL::int, 3::bigint, 2::bigint, 3::int),
              (1, 'Aki', 'Maduin', 'A', 5, 22500, 1, 8000, 3, 2, 3),
              (1, 'Aki', 'Maduin', 'A', 5, 6000, 4, NULL, 3, 2, 3),
              (1, 'Aki', 'Maduin', 'A', 5, 6001, 1, NULL, 3, 2, 3))
              AS t(owner_cid, name, world, account_label, sort_index, item_id, quantity, salvage_price, rewards, retainers, window_days);");
        var worn = await FleetReader.ReadHoldings(conn, "true", _ => { });
        Check(worn.Count == 2 && worn[0].Equipped && !worn[1].Equipped, "batch 3: equipped gear marked: " + string.Join(" | ", worn));
        var wornTotals = await FleetReader.ReadItemTotals(conn);
        Check(wornTotals.Single() == new ItemTotalRow(40000, false, 3, 1), "batch 3: the fleet's totals say how many are worn: " + wornTotals.Single());
        var shownPlaces = FleetOrder.WhereIsIt(worn.Where(h => !h.Equipped), id => "Ring").Single();
        Check(shownPlaces.Total == 2 && shownPlaces.Places.Single().Where == "Armoury chest", "batch 3: Where is it? without worn gear counts only the rest");
        var salvage = await FleetReader.ReadSalvage(conn);
        Check(salvage.Count == 2 && salvage[0].Character == "Bex" && salvage[0].Pieces.SequenceEqual(new long[] { 2, 0, 1, 0, 0, 0, 0, 1 }) && salvage[0].Gil == 61000
              && salvage[1].Character == "Aki" && salvage[1].SortIndex == int.MaxValue && salvage[1].Extravagant == 2,
            "batch 3: salvage read, in the Roster's order, each piece apart: " + string.Join(" | ", salvage.Select(x => $"{x.Character} {string.Join(",", x.Pieces)}")));
        var income = await FleetReader.ReadIncome(conn);
        var aki = income.Characters[0];
        Check(income.WindowDays == 3 && income.KitCost == 1730 && income.Characters.Count == 2 && aki.Enough && aki.Pieces == 7 && aki.FromChest == 2 && aki.FirstStored == 3
              && aki.KitsUsed == 4 && Math.Abs(aki.PerDay - 15451.4) < 1e-9 && !income.Characters[1].Enough && income.Characters[1].Cid == 18014498577484004 && income.Ventures.Count == 4,
            "batch 3: income read, with not enough history kept apart: " + string.Join(" | ", income.Characters));
        var prices = new Dictionary<int, double> { [5111] = 100, [6000] = 250, [6001] = 1000, [22500] = 3 };
        var ventureSummary = FleetReader.SummarizeVentures(income.Ventures, income.WindowDays, id => prices[id]).Single();
        Check(ventureSummary.Rewards == 3 && ventureSummary.Retainers == 2 && ventureSummary.Gil == 1000 + 8000 + 1000 + 1000 && Math.Abs(ventureSummary.PerDay - 11000 / 3.0) < 1e-9
              && ventureSummary.Top.Select(t => t.ItemId).SequenceEqual(new[] { 22500, 5111, 6000 }),
            "batch 3: venture rewards valued as the app (salvage at its fixed price), the three worth most, ties by item number: " + string.Join(",", ventureSummary.Top));
        Check(FleetOrder.MatchesWords("Diadochos Bottoms of Healing", "diadochos  healing") && FleetOrder.MatchesWords("Diadochos Bottoms of Healing", "heal diad")
              && !FleetOrder.MatchesWords("Diadochos Bottoms of Casting", "diadochos healing") && FleetOrder.MatchesWords("Anything", "  ") && FleetOrder.MatchesWords(null, ""),
            "batch 3: searching word by word, in any order, ignoring case");
        Check(FleetReader.JobRole("PLD") == ("tank", false) && FleetReader.JobRole("GLA") == ("tank", true) && FleetReader.JobRole("CNJ") == ("healer", true)
              && FleetReader.JobRole("SGE") == ("healer", false) && FleetReader.JobRole("ACN") == ("dps", true) && FleetReader.JobRole("BTN").Role == null,
            "retainers: a class's role for its colour, base classes marked, gatherers none");
        var fullSet = new List<FleetReader.GearPiece>
        {
            new(13, 90, 610, true), new(3, 90, 610, true), new(4, 90, 610, true), new(5, 90, 610, true), new(7, 90, 610, true), new(8, 90, 610, true),
            new(9, 90, 610, true), new(10, 90, 610, true), new(11, 90, 610, true), new(12, 90, 610, true), new(12, 90, 610, true),
        };
        Check(FleetReader.GearCheck("WHM", "level", 90, fullSet) == "meets" && FleetReader.GearCheck("WHM", "ilvl", 610, fullSet) == "meets"
              && FleetReader.GearCheck("WHM", null, null, fullSet) == null
              && FleetReader.GearCheck("WHM", "level", 100, fullSet) == "short"
              && FleetReader.GearCheck("WHM", "level", 90, fullSet.Take(10).ToList()) == "short"
              && FleetReader.GearCheck("PLD", "level", 90, fullSet) == "short"
              && FleetReader.GearCheck("WHM", "level", 90, fullSet.Select((piece, index) => index == 2 ? piece with { CanWear = false } : piece).ToList()) == "short",
            "retainers: gear meets its target when every slot is at it and for the class (a shield only for those with one), short otherwise, none without a target");
        var noonToday = new DateTime(2026, 11, 5, 12, 0, 0, DateTimeKind.Local);
        var laterToday = new DateTimeOffset(noonToday.AddHours(3)).ToUnixTimeSeconds();
        var tomorrowAt = new DateTimeOffset(noonToday.AddDays(1)).ToUnixTimeSeconds();
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        Check(FleetReader.FinishClock(laterToday, noonToday) == noonToday.AddHours(3).ToString("t", culture)
              && FleetReader.FinishClock(tomorrowAt, noonToday) == $"{noonToday.AddDays(1).ToString("ddd", culture)} {noonToday.AddDays(1).ToString("t", culture)}",
            "retainers: when a venture is done, with the day when it's not today");
        PartsAgainstTheApp();
        await PartsReadFromTheDatabase(conn);
        await Exec(conn, "DROP VIEW fleet_holdings_view, fleet_item_totals_view, fleet_salvage_view, fleet_income_view, fleet_venture_income_view");

        var reader = new FleetReader(() => null);
        reader.ReadIfDue(everything: true);
        Check(reader.Subs.Error != null && reader.Subs.Error.Contains("isn't set up") && reader.Roster.Error == reader.Subs.Error && !reader.Busy, "fleet views: no database set up says so, and reads nothing");
        await Exec(conn, "DROP VIEW fleet_subs_view, fleet_supplies_view, fleet_summary_view");
    }

    // 2.0 stage 1: the speed test reads what the in-game windows will need and times it. Every query runs (or, for a
    // table this scratch schema lacks, says why), reads every row, and the result reads plainly.
    private static async Task SpeedTestChecks(NpgsqlConnection conn, string connectionString)
    {
        await Exec(conn, "DROP TABLE IF EXISTS companion_pc_status");
        var results = await SpeedTest.RunAsync(connectionString, rounds: 2);
        Check(results.Select(r => r.Name).SequenceEqual(new[] { SpeedTest.ConnectName }.Concat(SpeedTest.Queries.Select(q => q.Name)).Append(SpeedTest.TotalName)),
            "speed test: the connection, each query and the total, in order: " + string.Join(", ", results.Select(r => r.Name)));
        foreach (var (name, sql) in SpeedTest.Queries)
        {
            var r = results.Single(x => x.Name == name);
            // The views come with the app's database update 14; this scratch schema has none, so they're reported missing.
            if (name == "PCs" || name.EndsWith("(view)"))
            {
                Check(r.Error != null && r.Error.Contains("does not exist"), $"speed test: a missing table or view is reported, not thrown ({name}): {r.Error}");
                continue;
            }
            var expected = (await Rows(conn, sql)).Count;
            Check(r.Error == null && r.Rows == expected && r.FirstMs > 0 && r.BestMs <= r.WorstMs,
                $"speed test: {name} reads all {expected} rows and is timed ({r.Rows} rows, {r.FirstMs:0.0}/{r.BestMs:0.0}/{r.WorstMs:0.0} ms, {r.Error ?? "no error"})");
        }
        var characters = results.Single(x => x.Name == "Characters");
        Check(characters.Rows > 0, $"speed test: there are characters to read ({characters.Rows})");
        var text = SpeedTest.ToText(new[]
        {
            new SpeedResult(SpeedTest.ConnectName, 0, 41.26, 0.31, 41.26, null),
            new SpeedResult("Subs", 1, 12.4, 3.25, 12.4, null),
            new SpeedResult("PCs", 0, 0, 0, 0, "relation \"companion_pc_status\" does not exist"),
        }, "v2.0.0.1, Remote database", 17.04, 16.9);
        Check(text == "XIV Fleet Companion v2.0.0.1, Remote database: speed test (3 rounds; first / best / worst)\n" +
                      "Connect: 41 ms / 0.3 ms / 41 ms\n" +
                      "Subs (1 row): 12 ms / 3.3 ms / 12 ms\n" +
                      "PCs: failed (relation \"companion_pc_status\" does not exist)\n" +
                      "Longest game frame while testing: 17 ms (before: 17 ms)",
            "speed test: the results read plainly: " + text.Replace("\n", " | "));
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
        await RunScript(conn, "tests/FleetWriterTests/old-database/001_metric_history.sql");
        await RunScript(conn, "tests/FleetWriterTests/old-database/004_character_current.sql");
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
        await RunScript(conn, "tests/FleetWriterTests/old-database/004_character_current.sql");
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
        await RunScript(conn, "tests/FleetWriterTests/old-database/006_submarine_slot.sql");
        await RunScript(conn, "tests/FleetWriterTests/old-database/006_submarine_slot.sql");
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

        // FCTracker's IPC: the same answers as its file, asked character by character; each FC is asked for once.
        var liveChars = new Dictionary<ulong, FCTrackerConnector.CharEntry>
        {
            [1] = new() { CID = 1, FC = 9000001 }, [2] = new() { CID = 2, FC = null }, [3] = new() { CID = 3, FC = 0 },
            [4] = new() { CID = 4, FC = 9000077 }, [5] = new() { CID = 5, FC = 9000002 }, [6] = new() { CID = 6, FC = 9000002 },
        };
        var liveFcs = new Dictionary<ulong, FCTrackerConnector.FcEntry>
        {
            [9000001] = new() { FCName = "Panda Co", FCPoints = 5 },
            [9000002] = new() { FCName = "Ember Haven", House = new() { City = 2, Ward = 6, Plot = 27, LastVisited = new DateTime(2026, 10, 1) } },
        };
        var fcAsked = new List<ulong>();
        var liveWithoutFc = new HashSet<ulong>();
        var live = FCTrackerConnector.ReadHousingLive(new ulong[] { 1, 2, 3, 4, 5, 6, 7 },
            cid => liveChars.GetValueOrDefault(cid),
            id => { fcAsked.Add(id); return liveFcs.GetValueOrDefault(id); }, liveWithoutFc);
        Check(string.Join(",", live.Keys.OrderBy(k => k)) == "1,5,6" && string.Join(",", liveWithoutFc.OrderBy(k => k)) == "2,3"
              && live[5].HouseWard == 7 && live[5].HousePlot == 28 && live[6].FcName == "Ember Haven"
              && live[5].HouseLastVisited == new DateTime(2026, 10, 1) && !live[1].HasHouse,
            $"FCTracker IPC: in an FC {string.Join(",", live.Keys)}, in none {string.Join(",", liveWithoutFc)}, same address as the file");
        Check(string.Join(",", fcAsked.OrderBy(k => k)) == "9000001,9000002,9000077", "FCTracker IPC: each Free Company is asked for once: " + string.Join(",", fcAsked));
        var failedWithoutFc = new HashSet<ulong>();
        var threw = false;
        try
        {
            FCTrackerConnector.ReadHousingLive(new ulong[] { 2, 5 }, cid => cid == 5 ? throw new InvalidOperationException("not ready") : liveChars.GetValueOrDefault(cid),
                id => liveFcs.GetValueOrDefault(id), failedWithoutFc);
        }
        catch (InvalidOperationException) { threw = true; }
        Check(threw && failedWithoutFc.Count == 0, "FCTracker IPC: a failed call leaves nothing half-filled (the file is read instead)");

        // Newer FCTrackers hand over everything in two calls; the one-at-a-time calls are then not used.
        var oneAtATime = 0;
        var allWithoutFc = new HashSet<ulong>();
        var all = FCTrackerConnector.ReadHousingLiveAll(new ulong[] { 1, 2, 3, 4, 5, 6, 7 }, () => liveChars, () => liveFcs,
            cid => { oneAtATime++; return null; }, id => { oneAtATime++; return null; }, allWithoutFc);
        Check(oneAtATime == 0 && string.Join(",", all.Keys.OrderBy(k => k)) == "1,5,6" && string.Join(",", allWithoutFc.OrderBy(k => k)) == "2,3"
              && all[5].HouseWard == 7 && all[6].FcName == "Ember Haven",
            $"FCTracker IPC, all at once: the same answers from two calls ({oneAtATime} single calls)");
        // An FCTracker without them (the call throws, or answers nothing) is asked one at a time.
        var fallback = FCTrackerConnector.ReadHousingLiveAll(new ulong[] { 1, 5 }, () => liveChars, () => throw new InvalidOperationException("no such call"),
            cid => liveChars.GetValueOrDefault(cid), id => liveFcs.GetValueOrDefault(id));
        var fallbackNull = FCTrackerConnector.ReadHousingLiveAll(new ulong[] { 1, 5 }, () => null, () => liveFcs,
            cid => liveChars.GetValueOrDefault(cid), id => liveFcs.GetValueOrDefault(id));
        Check(string.Join(",", fallback.Keys.OrderBy(k => k)) == "1,5" && string.Join(",", fallbackNull.Keys.OrderBy(k => k)) == "1,5",
            "FCTracker IPC: without the all-at-once calls, it asks one at a time");

        var liveFacts = new CheckReport.Facts { FcTrackerRunning = true, FcTrackerLive = true, FcTrackerPath = "x.json" };
        Check(CheckReport.FcTrackerLine(liveFacts) == "FC Tracker: running, details read directly from it"
              && CheckReport.FcTrackerLine(new CheckReport.Facts { FcTrackerRunning = true, FcTrackerFound = true, FcTrackerPath = "x.json" }).StartsWith("FC Tracker: running, a version without IPC"),
            "Check what I can see says whether FCTracker answered directly or its file was read");
        Check(CheckReport.FcTrackerLine(new CheckReport.Facts { FcTrackerRunning = true, FcTrackerLive = true, FcTrackerIpcVersion = 1 })
              == "FC Tracker: running, details read directly from it (IPC version 1)", "and FCTracker's IPC version, when it says");

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

        // Where characters last logged in: only the setup a character last logged in on syncs it.
        Check(await FleetWriter.ReadLoginOwnersAsync(conn, new ulong[] { 1 }) == null
              && !await FleetWriter.RecordLoginsAsync(conn, "PC1", "Account 1", new[] { KeyValuePair.Create(1UL, DateTime.UtcNow) })
              && await FleetWriter.RenameLoginSetupAsync(conn, "PC1", "a", "b") == 0,
            "before the database has the login table, nothing fails and no owners are known");
        await Exec(conn, @"CREATE TABLE companion_character_login (cid numeric PRIMARY KEY, pc_name text NOT NULL,
            account_label text NOT NULL, logged_in_at timestamptz NOT NULL)");
        const ulong berry = 18014498578000101, cal = 18014498578000102, dee = 18014498578000103;
        var early = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        await FleetWriter.RecordLoginsAsync(conn, "PC1", "Account R", new[] { KeyValuePair.Create(berry, early.AddHours(2)), KeyValuePair.Create(cal, early) });
        await FleetWriter.RecordLoginsAsync(conn, "PC1", "Account 2", new[] { KeyValuePair.Create(berry, early.AddHours(1)), KeyValuePair.Create(cal, early.AddHours(3)) });
        var owners = await FleetWriter.ReadLoginOwnersAsync(conn, new[] { berry, cal, dee });
        Check(owners != null && owners.Count == 2 && owners[berry] == new FleetWriter.LoginOwner("PC1", "Account R") && owners[cal] == new FleetWriter.LoginOwner("PC1", "Account 2"),
            "the latest login wins, whichever setup records it first; a character with no login is not listed");
        Check(!FleetWriter.OwnedElsewhere(owners, berry, "PC1", "Account R") && FleetWriter.OwnedElsewhere(owners, berry, "PC1", "Account 2")
              && FleetWriter.OwnedElsewhere(owners, berry, "PC2", "Account R")
              && !FleetWriter.OwnedElsewhere(owners, dee, "PC1", "Account 2") && !FleetWriter.OwnedElsewhere(null, berry, "PC1", "Account 2"),
            "a setup leaves alone a character that last logged in on another setup (another label or PC); one with no login, or no table, is synced as before");
        var moved = await FleetWriter.RenameLoginSetupAsync(conn, "PC1", "Account R", "Account 3");
        owners = await FleetWriter.ReadLoginOwnersAsync(conn, new[] { berry, cal });
        Check(moved == 1 && owners![berry] == new FleetWriter.LoginOwner("PC1", "Account 3") && owners[cal].AccountLabel == "Account 2",
            "a renamed account label takes its setup's characters along");
        var marin = 18014498578000104UL;
        var seen = new[] { KeyValuePair.Create(marin, early.AddHours(5)), KeyValuePair.Create(cal, early.AddHours(5)) };
        var recorded = CharacterChoice.LoginsToRecord(seen, cid => cid != marin);
        Check(recorded.Count == 1 && recorded[0].Key == cal, "a login is only recorded for a character this setup syncs (one unticked here claims nothing)");
        await FleetWriter.RecordLoginsAsync(conn, "LAPTOP", "Account 1", new[] { KeyValuePair.Create(marin, early) });
        var releasedNone = await FleetWriter.ReleaseLoginsAsync(conn, "DESKTOP-1", "Account 1", new[] { marin });
        var releasedOwn = await FleetWriter.ReleaseLoginsAsync(conn, "LAPTOP", "Account 1", new[] { marin });
        owners = await FleetWriter.ReadLoginOwnersAsync(conn, new[] { marin, cal });
        Check(releasedNone == 0 && releasedOwn == 1 && !owners!.ContainsKey(marin) && owners.ContainsKey(cal),
            "a setup gives up a character unticked there (only its own claim), so a setup that syncs it takes over");

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
        sched.Started(start.AddMinutes(7).AddSeconds(36));
        sched.RetryAfter(start.AddMinutes(8), TimeSpan.FromMinutes(1));
        sched.RequestSoon(start.AddMinutes(8));
        Check(sched.ShouldStart(start.AddMinutes(8).AddSeconds(6), five, busy: false) == "after logout", "a logout during a retry wait syncs at its own, sooner time");
        sched.Started(start.AddMinutes(8).AddSeconds(6));
        sched.RetryAfter(start.AddMinutes(8).AddSeconds(10), TimeSpan.FromMinutes(1));
        Check(sched.ShouldStart(start.AddMinutes(8).AddSeconds(50), five, busy: false) == null
              && sched.ShouldStart(start.AddMinutes(9).AddSeconds(10), five, busy: false) == "retry",
            "while the database is being moved, the sync is tried again a minute later, not at the next interval");

        // sql/005 removes the old history table, but only once nothing has written it for 10 minutes.
        string? refused = null;
        try { await RunScript(conn, "tests/FleetWriterTests/old-database/005_remove_character_snapshot.sql"); }
        catch (PostgresException ex) { refused = ex.MessageText; }
        var stillThere = Show(await Rows(conn, "SELECT to_regclass('companion_character_snapshot') IS NOT NULL"));
        Check(refused != null && refused.Contains("older than 0.4.0") && stillThere == "True",
            "005 refuses while the old table was written in the last 10 minutes, and changes nothing: " + refused);
        await Exec(conn, "UPDATE companion_character_snapshot SET snapshot_at = now() - interval '1 hour'");
        await RunScript(conn, "tests/FleetWriterTests/old-database/005_remove_character_snapshot.sql");
        await RunScript(conn, "tests/FleetWriterTests/old-database/005_remove_character_snapshot.sql");
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
            Bags = new()
            {
                Id = 1, Name = "Character", Items = 143, StoredAgo = TimeSpan.FromMinutes(5),
                Kinds = new() { ["armoury"] = 98, ["bags"] = 17, ["crystals"] = 18, ["equipped"] = 10 },
            },
            Retainers = { new() { Id = 2, Name = "Oldret", Items = 80, StoredAgo = TimeSpan.FromHours(1), Kinds = new() { ["bags"] = 77, ["for sale"] = 3 } }, new() { Id = 3, Name = "Newret" } },
            Chest = new() { Id = 9000001, Name = "FC chest" },
            Housing = new FCTrackerConnector.HousingInfo { FcId = 9000001, HasHouse = true, HouseCity = 2, HouseWard = 7, HousePlot = 28 },
        });
        facts.Characters.Add(new CheckReport.Character
        {
            Cid = 4, Name = "Bex", World = "Maduin", Bags = new() { Id = 4, Name = "Character", Items = 1, Kinds = new() { ["bags"] = 1 }, StoredAgo = TimeSpan.FromDays(3) }, SyncedAgo = TimeSpan.FromDays(3),
        });
        var report = CheckReport.ToText(facts);
        Check(report.Contains("AutoRetainer: running, 40 characters (38 left out in settings)") && report.Contains("2 things to fix (marked !)."),
            "the report starts with the other plugins and how many things to fix:\n" + report);
        Check(report.Contains("  Retainer Oldret: 80 items (bags 77, for sale 3), stored 1 hour ago") && report.Contains("  Retainer Newret: not seen, nothing stored")
              && report.Contains("  ! Open Newret at a summoning bell on this PC.") && report.Contains("  ! Open the Free Company chest of Panda Co on this PC.")
              && report.Contains("  House: Lavender Beds, Ward 7, Plot 28")
              && report.Contains("  Character: 143 items (bags 17, crystals 18, armoury 98, equipped 10), stored 5 minutes ago")
              && report.Contains("  Character: 1 item, stored 3 days ago"),
            "per character: each part and where its items are (no breakdown when they're all in one place), when it was stored, the house, and what to open:\n" + report);
        Check(CheckReport.Kind(0) == "bags" && CheckReport.Kind(3) == "bags" && CheckReport.Kind(2001) == "crystals" && CheckReport.Kind(4101) == "saddlebags"
              && CheckReport.Kind(3205) == "armoury" && CheckReport.Kind(1000) == "equipped" && CheckReport.Kind(2000) == "currency"
              && CheckReport.Kind(10003) == "bags" && CheckReport.Kind(12002) == "for sale" && CheckReport.Kind(12001) == "crystals"
              && CheckReport.Kind(20002) == "chest" && CheckReport.Kind(2501) == "glamour" && CheckReport.Kind(25010) == "house" && CheckReport.Kind(9999) == "other",
            "each AllaganTools container is named for the breakdown");
        Check(report.Contains("Bex @ Maduin\r\n  Last synced: 3 days ago".Replace("\r\n", Environment.NewLine)) && report.Contains("  Free Company: none")
              && CheckReport.ToFix(facts.Characters[1], facts).Count == 0, "a character with nothing to fix has no ! lines");

        // The FC chest holds the subs' supplies: it is only asked for when the FC has subs. FCTracker is
        // optional, so not finding it is said but not counted as something to fix.
        facts.Characters[0].Subs = 0;
        facts.FcTrackerFound = false;
        report = CheckReport.ToText(facts);
        Check(!report.Contains("Open the Free Company chest") && report.Contains("FC Tracker: not found, optional") && CheckReport.ToFixCount(facts) == 1,
            "an FC without subs is not asked to open its chest, and a missing FCTracker is not a thing to fix:\n" + report);
        facts.Characters[0].Subs = 4;
        facts.FcTrackerFound = true;

        // FCTracker loaded in this game or not (Dalamud's list of loaded plugins), and its file found or not.
        facts.FcTrackerRunning = true;
        var running = CheckReport.FcTrackerLine(facts);
        facts.FcTrackerRunning = false;
        var stopped = CheckReport.FcTrackerLine(facts);
        facts.FcTrackerFound = false;
        var missing = CheckReport.FcTrackerLine(facts);
        facts.FcTrackerRunning = null;
        facts.FcTrackerFound = true;
        Check(running.StartsWith("FC Tracker: running") && stopped.StartsWith("FC Tracker: not running in this game")
              && missing.StartsWith("FC Tracker: not found") && CheckReport.FcTrackerLine(facts).StartsWith("FC Tracker: found"),
            $"FCTracker is reported running, installed but not running, or not found:\n{running}\n{stopped}\n{missing}");

        var subFacts = new CheckReport.Facts { SubmarineTrackerRunning = true };
        var subRunning = CheckReport.SubmarineTrackerLine(subFacts);
        subFacts.SubmarineTrackerRunning = false;
        subFacts.SubmarineTrackerFound = true;
        var subStopped = CheckReport.SubmarineTrackerLine(subFacts);
        subFacts.SubmarineTrackerFound = false;
        var subMissing = CheckReport.SubmarineTrackerLine(subFacts);
        Check(subRunning == "Submarine Tracker: running" && subStopped.StartsWith("Submarine Tracker: not running in this game, optional")
              && subMissing.StartsWith("Submarine Tracker: not found, optional") && CheckReport.ToText(subFacts).Contains(subMissing)
              && CheckReport.ToolProblems(subFacts).All(p => !p.Contains("Submarine")),
            $"SubmarineTracker is reported running, installed but not running, or not found, and is never a thing to fix:\n{subRunning}\n{subStopped}\n{subMissing}");

        facts.AllaganToolsReady = false;
        facts.DatabaseProblem = "no saved connection (Settings → Database Connection).";
        report = CheckReport.ToText(facts);
        Check(report.Contains("! Allagan Tools is not running") && report.Contains("Database: not checked, no saved connection")
              && !report.Contains("Last synced") && !report.Contains("Newret: not seen") && CheckReport.ToFixCount(facts) == 2,
            "without AllaganTools or the database, the report says so once instead of listing every part:\n" + report);
        // sql/007: when each retainer's items were last seen. Without the column nothing fails.
        await Exec(conn, $"INSERT INTO companion_retainer_lookup (retainer_id, owner_cid, name) VALUES (77001, {owner}, 'Empty Ret') ON CONFLICT DO NOTHING");
        Check(!await FleetWriter.MarkRetainersSeenAsync(conn, new ulong[] { 77001 }), "before sql/007, marking retainers seen reports the column is missing");
        await RunScript(conn, "tests/FleetWriterTests/old-database/007_retainer_items_seen.sql");
        await RunScript(conn, "tests/FleetWriterTests/old-database/007_retainer_items_seen.sql");
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
        await RunScript(conn, "tests/FleetWriterTests/old-database/008_pcs_loot_ventures_listings.sql");
        await RunScript(conn, "tests/FleetWriterTests/old-database/008_pcs_loot_ventures_listings.sql");

        Check(await DetailsWriter.WritePcStatusAsync(conn, pc), "after sql/008 (run twice), a PC's status is written");
        pc.CharactersSynced = 2;
        pc.Problems.Clear();
        await DetailsWriter.WritePcStatusAsync(conn, pc);
        Check(await DetailsWriter.WritePcCheckAsync(conn, "DESKTOP-1", "Account 1", "0.10.0", "Nothing to fix.", 0), "a check result is written");
        Check(await DetailsWriter.WriteAutoRetainerNotRunningAsync(conn, "DESKTOP-1", "Account 1", "0.11.0")
              && Show(await Rows(conn, "SELECT autoretainer_ready, characters_synced, last_sync_at IS NOT NULL, problems[1], plugin_version FROM companion_pc_status")) ==
                 "False,2,True,AutoRetainer is not running: nothing can be synced.,0.11.0",
            "AutoRetainer not running: said, keeping the last sync's time and counts");
        await DetailsWriter.WriteAutoRetainerNotRunningAsync(conn, "LAPTOP", "Account 2", "0.11.0");
        Check(Show(await Rows(conn, "SELECT autoretainer_ready, last_sync_at IS NULL FROM companion_pc_status WHERE pc_name = 'LAPTOP'")) == "False,True",
            "a PC that has never synced is listed with AutoRetainer not running");
        await Exec(conn, "DELETE FROM companion_pc_status WHERE pc_name = 'LAPTOP'");
        pc.PluginVersion = "0.11.0";
        await DetailsWriter.WritePcStatusAsync(conn, pc);
        var pcRows = await Rows(conn, "SELECT pc_name, account_label, characters_synced, cardinality(problems), check_text, check_to_fix, last_sync_at IS NOT NULL FROM companion_pc_status");
        Check(Show(pcRows) == "DESKTOP-1,Account 1,2,0,Nothing to fix.,0,True",
            "one row per PC and account: the newest status, and the check kept beside it: " + Show(pcRows));
        await Exec(conn, "ALTER TABLE companion_pc_status ADD COLUMN db_user text");
        pc.PluginVersion = "0.14.0";
        Check(await DetailsWriter.WritePcStatusAsync(conn, pc)
              && Show(await Rows(conn, "SELECT plugin_version, db_user = current_user FROM companion_pc_status")) == "0.14.0,True",
            "with database update 10, the status also says which login this plugin uses (without it, the rest is written as before)");
        await DetailsWriter.WriteCharactersElsewhereAsync(conn, pc.PcName, pc.AccountLabel, 3);
        await Exec(conn, "ALTER TABLE companion_pc_status ADD COLUMN characters_elsewhere int");
        await DetailsWriter.WriteCharactersElsewhereAsync(conn, pc.PcName, pc.AccountLabel, 2);
        Check(Show(await Rows(conn, "SELECT characters_elsewhere FROM companion_pc_status")) == "2",
            "with database update 12, it says how many characters it left to another setup (without it, nothing fails)");

        // Moving the database to another PC (database update 10): the note the app leaves, and what the plugin does.
        Check(await DatabaseMove.ReadAsync(conn) == null && DatabaseMove.Decide(null) == DatabaseMove.Next.Sync,
            "without the move table there is no note, and the plugin syncs as usual");
        await Exec(conn, @"CREATE TABLE companion_database_move (id int PRIMARY KEY DEFAULT 1 CHECK (id = 1), state text NOT NULL,
            host text NOT NULL, port int NOT NULL, from_host text, started_at timestamptz NOT NULL DEFAULT now(),
            updated_at timestamptz NOT NULL DEFAULT now(), moved_at timestamptz)");
        Check(await DatabaseMove.ReadAsync(conn) == null, "an empty move table: no note");
        await Exec(conn, "INSERT INTO companion_database_move (state, host, port) VALUES ('pausing', '100.64.1.9', 5432)");
        var note = await DatabaseMove.ReadAsync(conn);
        Check(note != null && note.State == "pausing" && note.Fresh && DatabaseMove.Decide(note) == DatabaseMove.Next.Wait,
            "while the app is copying the database, the plugin waits");
        await Exec(conn, "UPDATE companion_database_move SET updated_at = now() - interval '6 minutes'");
        Check(DatabaseMove.Decide(await DatabaseMove.ReadAsync(conn)) == DatabaseMove.Next.Sync,
            "a pause the app stopped refreshing (closed mid-move) is ignored, so syncing never stops for good");
        await Exec(conn, "UPDATE companion_database_move SET state = 'moved', moved_at = now()");
        note = await DatabaseMove.ReadAsync(conn);
        Check(note != null && note.Host == "100.64.1.9" && note.Port == 5432 && DatabaseMove.Decide(note) == DatabaseMove.Next.Follow,
            "once moved, the plugin follows to the new address (however old the note is)");
        await Exec(conn, "UPDATE companion_database_move SET state = 'arrived'");
        Check(DatabaseMove.Decide(await DatabaseMove.ReadAsync(conn)) == DatabaseMove.Next.Sync,
            "in the new database the note says it arrived, and the plugin syncs to it");
        Check(DatabaseMove.Decide(new DatabaseMove.Note { State = "moved", Host = " ", Port = 5432 }) == DatabaseMove.Next.Sync,
            "a moved note without an address is not followed");
        Check(new DatabaseMove.UnreachableException("100.64.1.9:5432", "timeout").Message.Contains("moved to 100.64.1.9:5432, but this PC can't reach it"),
            "an unreachable new address is said plainly");
        await Exec(conn, "DROP TABLE companion_database_move");

        // Why a sync didn't get through, from the errors Npgsql really throws.
        async Task<Exception?> FailToOpen(string connectionString)
        {
            try
            {
                await using var c = new NpgsqlConnection(connectionString);
                await c.OpenAsync();
                return null;
            }
            catch (Exception ex)
            {
                return ex;
            }
        }
        var offline = await FailToOpen("Host=127.0.0.1;Port=1;Username=x;Password=x;Database=x;Timeout=3");
        Check(offline != null && SyncFailure.Describe(offline) == SyncFailure.CantReach && SyncFailure.IsExpected(offline),
            "the database PC being off is said plainly, and logged once: " + offline?.GetType().Name);
        var wrongName = await FailToOpen(new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FLEET_TEST_DB") ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres") { Database = "no_such_fleet_database" }.ConnectionString);
        Check(wrongName != null && SyncFailure.Describe(wrongName) == SyncFailure.NoDatabase && SyncFailure.IsExpected(wrongName),
            "a database name that doesn't exist is said plainly: " + wrongName?.Message);
        var refused = new PostgresException("password authentication failed for user \"x\"", "FATAL", "FATAL", PostgresErrorCodes.InvalidPassword);
        Check(SyncFailure.Describe(refused) == SyncFailure.WrongLogin && SyncFailure.IsExpected(refused), "a wrong password is said plainly");
        var moving = new DatabaseMove.WaitException();
        var away = new DatabaseMove.UnreachableException("100.64.1.9:5432", "timeout");
        Check(SyncFailure.Describe(moving) == moving.Message && SyncFailure.Describe(away) == away.Message && SyncFailure.IsExpected(away),
            "a database move says what it says already");
        var bug = new InvalidOperationException("Sequence contains no elements");
        Check(SyncFailure.Describe(bug) == "Sequence contains no elements" && !SyncFailure.IsExpected(bug)
              && !SyncFailure.IsExpected(new PostgresException("syntax error", "ERROR", "ERROR", PostgresErrorCodes.SyntaxError)),
            "anything else may be a bug, so it is logged in full every time");

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
        await RunScript(conn, "tests/FleetWriterTests/old-database/009_voyage_sector_order.sql");
        await RunScript(conn, "tests/FleetWriterTests/old-database/009_voyage_sector_order.sql");
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
