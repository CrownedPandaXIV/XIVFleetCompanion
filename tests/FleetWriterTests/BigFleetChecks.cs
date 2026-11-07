using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Npgsql;
using XIVFleetCompanion;

// A big made-up fleet (100 characters in 25 Free Companies, 400 subs, 800 retainers, full bags, 90 days of history,
// voyage loot and venture rewards) synced into a PostgreSQL database and into a fleet file the way the plugin does, then
// every page timed on both. The times are printed; a page slower than a few seconds fails, as something has gone wrong.
internal static partial class Program
{
    private const int BigCharacters = 100, BigSubsEach = 4, BigRetainersEach = 8, BigDays = 90;
    private static readonly TimeSpan BigPageLimit = TimeSpan.FromSeconds(3);

    private static async Task BigFleetChecks(NpgsqlConnection admin, string given)
    {
        await Exec(admin, "DROP DATABASE IF EXISTS big_pg WITH (FORCE)");
        await Exec(admin, "CREATE DATABASE big_pg");
        var folder = Path.Combine(Path.GetTempPath(), $"big-{Guid.NewGuid():N}");
        try
        {
            await using var pg = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(given) { Database = "big_pg", Pooling = false }.ConnectionString);
            await pg.OpenAsync();
            await DatabaseUpkeep.EnsureAsync(pg, DatabaseUpkeep.Files.Embedded(), "big test", wait: true);
            var path = Path.Combine(folder, "fleet.db");
            await using var file = await FileDatabase.OpenAsync(path, Array.Empty<string>());
            await FileDatabase.EnsureAsync(file, FileDatabase.Files.Embedded(), "big test");

            var sync = new Dictionary<string, double>();
            foreach (var (name, conn) in new (string, DbConnection)[] { ("PostgreSQL", pg), ("file", file) })
            {
                var clock = Stopwatch.StartNew();
                await FillBigFleet(conn);
                sync[name] = clock.Elapsed.TotalSeconds;
            }
            var rows = await Count(file, "SELECT (SELECT count(*) FROM companion_metric_history) + (SELECT count(*) FROM companion_voyage_loot) + " +
                                         "(SELECT count(*) FROM companion_venture_result) + (SELECT count(*) FROM companion_inventory_snapshot)");
            Check(await Count(file, "SELECT count(*) FROM companion_character_current") == BigCharacters && await Count(pg, "SELECT count(*) FROM companion_character_current") == BigCharacters,
                $"big fleet: {BigCharacters} characters and {rows:N0} history, loot, venture and item rows written to both " +
                $"(PostgreSQL {sync["PostgreSQL"]:0.0} s, file {sync["file"]:0.0} s; file {new FileInfo(path).Length / 1024.0 / 1024.0:0.0} MB)");

            // One sync of every character, as the plugin does every few minutes.
            var chars = BigChars(1);
            foreach (var (name, conn) in new (string, DbConnection)[] { ("PostgreSQL", pg), ("file", file) })
            {
                var clock = Stopwatch.StartNew();
                await FleetWriter.WriteCurrentCharactersAsync(conn, chars);
                foreach (var c in chars) await FleetWriter.WriteSubmarinesAsync(conn, c.Cid, BigSubs(c.Cid, 1));
                var ms = clock.Elapsed.TotalMilliseconds;
                Check(ms < BigPageLimit.TotalMilliseconds * 3, $"big fleet: a sync of every character's details and subs on {name}: {ms:0} ms");
            }

            var nowMs = (DateTime.UtcNow - DateTime.UnixEpoch).TotalMilliseconds;
            var pages = new List<(string Name, Func<DbConnection, Task<object?>> Read)>
            {
                ("Subs", async c => await FleetReader.ReadSubs(c)),
                ("Supplies", async c => await FleetReader.ReadSupplies(c)),
                ("Summary", async c => await FleetReader.ReadSummary(c)),
                ("what needs attention", async c => await FleetReader.ReadAlerts(c)),
                ("Roster", async c => await FleetReader.ReadRoster(c)),
                ("Retainers", async c => await FleetReader.ReadRetainers(c)),
                ("PCs", async c => await FleetReader.ReadPcs(c)),
                ("Free Companies", async c => await FleetReader.ReadFcs(c)),
                ("Inventory", async c => await FleetReader.ReadHoldings(c, "true")),
                ("item totals", async c => await FleetReader.ReadItemTotals(c)),
                ("Gil & Market", async c => (await FleetReader.ReadMarketData(c)).Read),
                ("routes' inputs", async c => await FleetReader.ReadRoutesInputs(c)),
                ("forecast accuracy", async c => await FleetReader.ReadForecasts(c)),
                ("Loot per Voyage", async c => await FleetReader.ReadLoot(c)),
                ("Parts", async c => (await FleetReader.ReadParts(c)).Needs),
                ("Salvage", async c => await FleetReader.ReadSalvage(c)),
                ("Income", async c => await FleetReader.ReadIncome(c)),
                ("venture income", async c => await FleetReader.ReadVentures(c)),
                ("Shopping List", async c => await FleetReader.ReadShopping(c)),
                ("Trends: what's recorded", async c => await FleetReader.ReadTrendsMeta(c)),
                ("Trends: gil, 90 days, whole fleet", async c => await FleetReader.ReadSeries(c, new TrendRequest(nowMs - BigDays * 86_400_000.0, nowMs, 90, new() { new("gil", "character") }, null))),
                ("Trends: gil, 90 days, 10 characters", async c => await FleetReader.ReadSeries(c, new TrendRequest(nowMs - BigDays * 86_400_000.0, nowMs, 90,
                    new() { new("gil", "character") }, Enumerable.Range(0, 10).Select(i => BigCid(i).ToString()).ToList()))),
            };
            var report = new List<string>();
            foreach (var (page, read) in pages)
            {
                var times = new List<double>();
                foreach (var conn in new DbConnection[] { pg, file })
                {
                    await read(conn);   // warm
                    var clock = Stopwatch.StartNew();
                    await read(conn);
                    times.Add(clock.Elapsed.TotalMilliseconds);
                }
                report.Add($"{page}: PostgreSQL {times[0]:0} ms, file {times[1]:0} ms");
                Check(times.All(t => t < BigPageLimit.TotalMilliseconds), $"big fleet: {page} reads in under {BigPageLimit.TotalSeconds:0} s on both (PostgreSQL {times[0]:0} ms, file {times[1]:0} ms)");
            }
            Console.WriteLine("big fleet timings:\n  " + string.Join("\n  ", report));
        }
        finally
        {
            System.Data.SQLite.SQLiteConnection.ClearAllPools();
            try { Directory.Delete(folder, true); } catch (IOException) { }
            await Exec(admin, "DROP DATABASE IF EXISTS big_pg WITH (FORCE)");
        }
    }

    private static ulong BigCid(int i) => 18014498578100000UL + (ulong)i;
    private static ulong BigFc(int i) => 9232379236109500000UL + (ulong)(i / 4);

    private static List<FleetWriter.CharacterSnapshot> BigChars(int round) => Enumerable.Range(0, BigCharacters).Select(i => new FleetWriter.CharacterSnapshot
    {
        Cid = BigCid(i), Name = $"Char {i}", World = new[] { "Maduin", "Golem", "Cactuar", "Leviathan" }[i % 4], Gil = 1_000_000 + i * 1000 + round,
        Ceruleum = 500 + i, RepairKits = 100 + i, RetainerCount = BigRetainersEach, SubmarineCount = BigSubsEach, AccountLabel = $"acct{i % 3}",
        FcId = BigFc(i), NumSubSlots = 4,
    }).ToList();

    private static List<FleetWriter.SubmarineRecord> BigSubs(ulong cid, int round) => Enumerable.Range(1, BigSubsEach).Select(s => new FleetWriter.SubmarineRecord
    {
        SubName = $"Sub {s}", Level = 50 + s * 10, Part1 = 21794 + s, Part2 = 21795, Part3 = 21796, Part4 = 21797, Points = new byte[] { 15, 10, (byte)(13 + s) },
        ReturnTime = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + s * 3600 - round, CurrentExp = 1000 * s, NextLevelExp = 500_000, Slot = s,
    }).ToList();

    // The big fleet, the way the plugin writes it, plus months of history written in bulk.
    private static async Task FillBigFleet(DbConnection conn)
    {
        var file = Db.IsFile(conn);
        var chars = BigChars(0);
        await FleetWriter.WriteCurrentCharactersAsync(conn, chars);
        var now = DateTime.UtcNow;
        var nowUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        for (var i = 0; i < BigCharacters; i++)
        {
            var cid = BigCid(i);
            await FleetWriter.WriteSubmarinesAsync(conn, cid, BigSubs(cid, 0));
            var retainers = Enumerable.Range(0, BigRetainersEach).Select(r => new FleetWriter.RetainerRecord
            {
                RetainerId = cid * 10 + (ulong)r, Name = $"Ret {i}-{r}", Job = (uint)(16 + r % 3), Gil = (uint)(1000 * r), HasVenture = true, VentureId = 395,
                VentureBeginsAt = nowUnix - 3600, VentureEndsAt = nowUnix + 600 * r, Level = 90 + r % 10, HireOrderIndex = r,
            }).ToList();
            await FleetWriter.WriteRetainersAsync(conn, cid, retainers);
            var sources = new List<FleetWriter.InventorySource>
            {
                new() { Id = cid, Name = $"Char {i}", SeenItems = Enumerable.Range(0, 80).Select(k => new FleetWriter.InventoryItem
                    { SortedContainer = (uint)(k / 35), SortedSlotIndex = k % 35, ItemId = (uint)(22500 + k % 40), Quantity = (uint)(1 + k) }).ToList() },
            };
            sources.AddRange(retainers.Select(r => new FleetWriter.InventorySource
            {
                Id = r.RetainerId, Name = r.Name, SeenItems = Enumerable.Range(0, 50).Select(k => new FleetWriter.InventoryItem
                    { SortedContainer = (uint)(10000 + k / 25), SortedSlotIndex = k % 25, ItemId = (uint)(5100 + k % 60), Quantity = (uint)(10 + k) }).ToList(),
            }));
            await FleetWriter.WriteInventoryAsync(conn, cid, FleetWriter.PlanInventory(cid, sources, retainerListKnown: true));
            if (i % 4 == 0)
            {
                await FleetWriter.WriteHousingAsync(conn, cid, new FCTrackerConnector.HousingInfo { FcId = BigFc(i), FcName = $"FC {i / 4}", FcPoints = 50_000 + i, FcRank = 8,
                    TotalMembers = 4, FcMaster = $"Char {i}" });
                await FleetWriter.WriteFcInventoryAsync(conn, BigFc(i), new List<FleetWriter.InventoryItem>
                    { new() { SortedContainer = 20000, SortedSlotIndex = 0, ItemId = 10155, Quantity = 999 }, new() { SortedContainer = 20000, SortedSlotIndex = 1, ItemId = 10373, Quantity = 500 } });
            }
        }

        // 90 days of history: each character's gil, ceruleum, repair kits and subs' rank and experience, a value a day.
        object At(DateTime t) => file ? Db.Unix(t) : t;
        object Fc(ulong id) => file ? Db.FcText(id)! : (decimal)id;
        var history = new List<object?[]>();
        for (var i = 0; i < BigCharacters; i++)
            for (var d = BigDays; d >= 1; d--)
            {
                var t = now.AddDays(-d);
                history.Add(new object?[] { At(t), "character", (long)BigCid(i), "gil", 1_000_000.0 + i * 1000 + d * 37, null });
                history.Add(new object?[] { At(t), "character", (long)BigCid(i), "ceruleum", 500.0 + (d * 13) % 300, null });
                history.Add(new object?[] { At(t), "character", (long)BigCid(i), "repair_kits", 100.0 + (d * 7) % 90, null });
                for (var s = 1; s <= BigSubsEach; s++)
                {
                    history.Add(new object?[] { At(t), "character", (long)BigCid(i), $"sub_rank:{s}", (double)(50 + s * 10 - d / 10), null });
                    history.Add(new object?[] { At(t), "character", (long)BigCid(i), $"sub_exp:{s}", (double)(1000 * s + (BigDays - d) * 4000), null });
                }
            }
        await Bulk(conn, "companion_metric_history", new[] { "recorded_at", "subject_type", "subject_id", "metric", "value", "label" }, history);
        await MetricWriter.WriteMetricsAsync(conn, chars.Select(c => new MetricWriter.MetricPoint { SubjectType = "character", SubjectId = c.Cid, Metric = "gil", Value = c.Gil }).ToList());

        // Each sub's voyages: two a day, three sectors each, for 90 days.
        var loot = new List<object?[]>();
        for (var fc = 0; fc < BigCharacters / 4; fc++)
            for (var s = 1; s <= BigSubsEach * 4; s++)
                for (var v = 0; v < BigDays * 2; v++)
                    for (var leg = 1; leg <= 3; leg++)
                        loot.Add(new object?[] { Fc(BigFc(fc * 4)), (long)s, At(DateTime.UnixEpoch.AddSeconds(nowUnix - v * 43_200 - s * 60)), (long)(10 + leg), $"Sub {(s - 1) % 4 + 1}",
                            (long)(22500 + leg), (long)(1 + v % 3), file ? 0L : false, 0L, 0L, file ? 0L : false, file ? 1L : true, (long)leg });
        await Bulk(conn, "companion_voyage_loot", new[] { "fc_id", "sub_register", "returned_at", "sector", "sub_name", "primary_item", "primary_count", "primary_hq",
            "additional_item", "additional_count", "additional_hq", "valid", "leg" }, loot);

        // Each retainer's ventures: one a day for 90 days.
        var ventures = new List<object?[]>();
        for (var i = 0; i < BigCharacters; i++)
            for (var r = 0; r < BigRetainersEach; r++)
                for (var d = 0; d < BigDays; d++)
                    ventures.Add(new object?[] { (long)BigCid(i), $"Ret {i}-{r}", At(now.AddDays(-d).AddMinutes(-r)), (long)(5111 + d % 20), file ? 0L : false, 20L, 395L });
        await Bulk(conn, "companion_venture_result", new[] { "owner_cid", "retainer_name", "received_at", "item_id", "hq", "quantity", "venture_id" }, ventures);
        if (!file) await Exec((NpgsqlConnection)conn, "ANALYZE");
    }

    // Many rows at once: a few hundred to a statement, in one transaction.
    private static async Task Bulk(DbConnection conn, string table, string[] columns, List<object?[]> rows)
    {
        await using var tx = await conn.BeginTransactionAsync();
        var per = Math.Max(1, 900 / columns.Length);
        for (var start = 0; start < rows.Count; start += per)
        {
            await using var cmd = Db.Command(conn, "", tx);
            var values = new List<string>();
            var n = 0;
            foreach (var row in rows.Skip(start).Take(per))
            {
                var marks = new List<string>();
                foreach (var v in row)
                {
                    var name = $"b{n++}";
                    Db.Add(cmd, name, v);
                    marks.Add("@" + name);
                }
                values.Add("(" + string.Join(", ", marks) + ")");
            }
            cmd.CommandText = $"INSERT INTO {table} ({string.Join(", ", columns)}) VALUES {string.Join(", ", values)}";
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }
}
