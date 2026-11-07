// Checks the plugin's database writes (FleetWriter.cs) against a scratch Postgres: every table is
// created in a throwaway schema with the same column types as the real database, written to,
// read back and compared. Never point this at the real database.
//
//   FLEET_TEST_DB="Host=localhost;Username=postgres;Password=postgres;Database=postgres" dotnet run
using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using XIVFleetCompanion;

internal static class Program
{
    private static int failures;
    private static int passes;

    // Market prices: the plugin's Universalis code, given the same canned listings as the app's (through a stand-in for the
    // web), works out the same rivals, undercut listings and venture values (the app's tests/market-fixture.js).
    private sealed class CannedUniversalis : System.Net.Http.HttpMessageHandler
    {
        private readonly System.Text.Json.JsonElement c;
        public int Asked;
        public CannedUniversalis(System.Text.Json.JsonElement fixtureCase) => c = fixtureCase;
        protected override Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken token)
        {
            System.Threading.Interlocked.Increment(ref Asked);
            var path = request.RequestUri!.AbsolutePath;
            System.Net.Http.HttpResponseMessage Reply(int status, string body) => new((System.Net.HttpStatusCode)status) { Content = new System.Net.Http.StringContent(body) };
            if (path == "/api/v2/data-centers") return Task.FromResult(Reply(200, c.GetProperty("dataCenters").GetRawText()));
            if (path == "/api/v2/worlds") return Task.FromResult(Reply(200, c.GetProperty("worlds").GetRawText()));
            var m = System.Text.RegularExpressions.Regex.Match(path, @"^/api/v2/([^/]+)/([0-9,]+)$");
            if (!m.Success) return Task.FromResult(Reply(404, "{}"));
            var scope = Uri.UnescapeDataString(m.Groups[1].Value);
            var ids = m.Groups[2].Value.Split(',').ToList();
            var canned = c.GetProperty("canned");
            var has = canned.TryGetProperty(scope, out var byItem);
            var found = ids.Where(id => has && byItem.TryGetProperty(id, out _)).ToList();
            if (found.Count == 0) return Task.FromResult(Reply(404, "{}"));
            string Item(string id) => $"{{\"itemID\":{id},\"listings\":{byItem.GetProperty(id).GetRawText()}}}";
            return Task.FromResult(Reply(200, ids.Count == 1 ? Item(ids[0]) : $"{{\"items\":{{{string.Join(",", found.Select(id => $"\"{id}\":{Item(id)}"))}}}}}"));
        }
    }

    // Universalis asked the quick way: each world, data center and region once, only for the items wanted on its worlds,
    // several requests at a time but never more than 4.
    private sealed class SlowUniversalis : System.Net.Http.HttpMessageHandler
    {
        public readonly System.Collections.Concurrent.ConcurrentBag<string> Paths = new();
        private int now;
        public int Most;
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken token)
        {
            var path = Uri.UnescapeDataString(request.RequestUri!.AbsolutePath);
            if (path.EndsWith("data-centers") || path.EndsWith("worlds")) return new(System.Net.HttpStatusCode.InternalServerError);
            Paths.Add(path);
            var at = System.Threading.Interlocked.Increment(ref now);
            lock (Paths) Most = Math.Max(Most, at);
            await Task.Delay(50, token);
            System.Threading.Interlocked.Decrement(ref now);
            return new(System.Net.HttpStatusCode.NotFound) { Content = new System.Net.Http.StringContent("{}") };
        }
    }

    private static async Task MarketAsksEachPlaceOnce()
    {
        var handler = new SlowUniversalis();
        var universalis = new Universalis(new System.Net.Http.HttpClient(handler), "https://universalis.test");
        // Maduin and Halicarnassus share Dynamis; Zalera is on Crystal; all three in North America. 120 items on Maduin (3 batches).
        var wanted = Enumerable.Range(1, 120).Select(id => ((string?)"Maduin", id))
            .Concat(new[] { ((string?)"Halicarnassus", 500), ((string?)"Halicarnassus", 1), ((string?)"Zalera", 700), ((string?)null, 900) }).ToList();
        var market = await universalis.MarketFor(wanted);
        var paths = handler.Paths.ToList();
        static List<int> Ids(string path) => path.Split('/').Last().Split(',').Select(x => int.Parse(x, System.Globalization.CultureInfo.InvariantCulture)).ToList();
        List<int> AskedOf(string place) => paths.Where(x => x.StartsWith($"/api/v2/{place}/")).SelectMany(Ids).OrderBy(x => x).ToList();
        var maduin = Enumerable.Range(1, 120).ToList();
        Check(AskedOf("Halicarnassus").SequenceEqual(new[] { 1, 500 }) && AskedOf("Zalera").SequenceEqual(new[] { 700 }) && AskedOf("Maduin").SequenceEqual(maduin)
              && AskedOf("Dynamis").SequenceEqual(maduin.Append(500)) && AskedOf("Crystal").SequenceEqual(new[] { 700 })
              && AskedOf("North-America").SequenceEqual(maduin.Append(500).Append(700))
              && paths.Count == 3 + 1 + 1 + 3 + 1 + 3 && handler.Most > 1 && handler.Most <= 4
              && market.Count == 3 && market["Halicarnassus"].Names.Dc == "Dynamis" && !paths.Any(x => x.Contains("900")),
            $"market: Universalis asked each world, data center and region once, only for the items wanted there, at most 4 at a time ({paths.Count} requests, {handler.Most} at once)");
    }

    // The window draws every frame: the market's working out (each listing against the cheapest rivals, every venture
    // reward at market prices) is done once per read and kept, not done again each frame. A fleet about the size of a big
    // one: 1,500 listings, 160 retainers, 60 characters with 150 kinds of venture reward on 5 worlds.
    private static void MarketWorkedOutOncePerRead()
    {
        var rnd = new Random(1);
        var worlds = new[] { "Maduin", "Halicarnassus", "Zalera", "Siren", "Gilgamesh" };
        var exclude = Enumerable.Range(0, 160).Select(i => Universalis.RetainerKey($"Ret{i}", worlds[i % 5])).ToHashSet();
        List<MarketListing> Ls(string? w) => Enumerable.Range(0, 20).Select(k => new MarketListing(rnd.Next(100, 9999), 1, k % 3 == 0, $"Seller{k}", w)).ToList();
        var items = Enumerable.Range(1, 400).ToList();
        WorldMarket Market(string w) => new(new MarketPlaceNames(w, "Dynamis", "NA"),
            Universalis.Scopes.ToDictionary(s => s, s => items.ToDictionary(id => id, id => Ls(s == "world" ? null : "Elsewhere"))), null);
        var live = new LiveMarket(worlds.ToDictionary(w => w, Market), exclude, null);
        var listings = Enumerable.Range(0, 1500).Select(i => new ListingRow(i, 0, i % 20, items[i % 400], 1, false, 500, null, $"Ret{i % 160}", "A", worlds[i % 5], null, null, 500, 3, false)).ToList();
        var read = new MarketRead(new MarketSettings(7, true), new List<MarketAccountRow>(), listings, live);
        var rows = Enumerable.Range(0, 60).SelectMany(ch => items.Take(150).Select(id => new VentureIncomeRow(ch, $"Char{ch}", worlds[ch % 5], "A", ch, id, 5, null, 1, 1, 1))).ToList();
        // In the background: asking takes no time on the game's thread, and the answers follow.
        var cache = new MarketCache();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        cache.Undercut(read);
        cache.VenturesPerDay(read, rows, 7, _ => 10);
        var asking = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        while (cache.Working) System.Threading.Thread.Sleep(1);
        var background = sw.Elapsed.TotalMilliseconds;
        var undercut = cache.Undercut(read);
        var perDay = cache.VenturesPerDay(read, rows, 7, _ => 10);
        sw.Restart();
        var same = true;
        for (var frame = 0; frame < 600; frame++)
            same &= cache.Undercut(read) == undercut && ReferenceEquals(cache.VenturesPerDay(read, rows, 7, _ => 10), perDay)
                    && ReferenceEquals(cache.Compared(read), cache.Compared(read));
        var perFrame = sw.Elapsed.TotalMilliseconds / 600;
        // The same answers as working each one out directly (as the app does), every character and scope.
        var direct = MarketMath.Compare(listings, live.Markets, exclude).Count(x => x.Undercut);
        var vendor = items.Take(150).ToDictionary(id => id, _ => 10.0);
        var allMatch = perDay != null && Enumerable.Range(0, 60).All(ch => Universalis.Scopes.All(sc => Math.Abs(perDay[ch][sc]
            - MarketMath.VentureGil(rows.Where(r => r.Cid == ch).Select(r => (r.ItemId, r.Quantity, r.HqQuantity)), live.Markets[worlds[ch % 5]], sc, vendor, exclude) / 7) < 1e-6));
        // A new read (new prices in): worked out again (at once here), the answers from before shown meanwhile.
        var now = new MarketCache(work => { work(); return Task.CompletedTask; });
        now.VenturesPerDay(read, rows, 7, _ => 10);
        var old = now.VenturesPerDay(read, rows, 7, _ => 10);
        var newer = read with { Live = live with { } };
        var meanwhile = now.VenturesPerDay(newer, rows, 7, _ => 10);
        var renewed = now.VenturesPerDay(newer, rows, 7, _ => 10);
        var again = old != null && ReferenceEquals(meanwhile, old) && renewed != null && !ReferenceEquals(renewed, old) && Math.Abs(renewed[3]["region"] - old[3]["region"]) < 1e-9
                    && now.Undercut(newer) == direct && new MarketCache().Undercut(read with { Live = null }) == 0;
        Check(same && again && undercut == direct && allMatch && perFrame < 0.5 && asking < 50,
            $"market: worked out in the background once per read and kept ({asking:0} ms on the game's thread, {background:0} ms in the background, {perFrame * 1000:0.0} µs a frame after; {undercut} undercut, the same as working it out directly); a new read works it out again");
    }

    // The Gil & Market page read through the database (stand-ins for the views with their PostgreSQL types), Live Market
    // Prices off: nothing is asked of Universalis.
    private static async Task MarketReadFromTheDatabase(NpgsqlConnection conn)
    {
        await Exec(conn, @"DROP TABLE IF EXISTS fleet_market_settings_view, fleet_market_accounts_view, fleet_market_listings_view;
            CREATE TABLE fleet_market_settings_view (stale_days int, live_prices boolean);
            CREATE TABLE fleet_market_accounts_view (account_label text, characters bigint, retainers bigint, character_gil numeric, retainer_gil numeric, total numeric);
            CREATE TABLE fleet_market_listings_view (retainer_id numeric, owner_cid numeric, slot int, item_id int, quantity int, hq boolean, unit_price bigint,
              first_seen_at timestamptz, retainer_name text, owner_name text, world text, account_label text, sort_index int, value bigint, age_days numeric, stale boolean);
            INSERT INTO fleet_market_settings_view VALUES (7, false);
            INSERT INTO fleet_market_accounts_view VALUES ('A', 2, 3, 1000, 507, 1507), ('Ungrouped', 1, 0, 25, 0, 25);
            INSERT INTO fleet_market_listings_view VALUES
              (33777097243660300, 18014498578000001, 0, 5111, 3, true, 450, now() - interval '9 days', 'Ret A', 'Aki', 'Maduin', 'A', 2, 1350, 9.000001, true),
              (33777097243660301, 18014498578000001, 4, 27800, 1, false, 125000, now() - interval '2 hours', 'Ret B', 'Aki', 'Maduin', 'A', 2, 125000, 0.0833, false),
              (33777097243660302, 999, 1, 40001, 99, false, 1, now(), NULL, NULL, NULL, NULL, NULL, 99, 0, false)");
        var counting = new CannedUniversalis(default);
        var m = await FleetReader.ReadMarket(conn, new Universalis(new System.Net.Http.HttpClient(counting), "https://universalis.test"));
        Check(m.Settings == new MarketSettings(7, false) && m.Accounts.Count == 2 && m.Accounts[0].Total == 1507 && m.Listings.Count == 3
              && m.Listings.Count(l => l.Stale) == 1 && m.Listings[0].RetainerId == 33777097243660300 && m.Listings[0].Hq && Math.Abs(m.Listings[0].AgeDays - 9) < 0.001
              && m.Listings[2].OwnerName == null && m.Live == null && counting.Asked == 0,
            $"market: the Gil & Market page reads the database's views (gil per account, listings unsold for a while), and with Live Market Prices off asks Universalis nothing ({m.Listings.Count} listings, {counting.Asked} asked)");
        await Exec(conn, "DROP TABLE fleet_market_settings_view, fleet_market_accounts_view, fleet_market_listings_view");
    }

    private static async Task MarketAgainstTheApp()
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "market-fixture.json");
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
        static string Price(MarketPrice? p) => p == null ? "none" : $"{p.Price.ToString(System.Globalization.CultureInfo.InvariantCulture)}@{p.World ?? "-"}";
        static string PriceJson(System.Text.Json.JsonElement e) => e.ValueKind == System.Text.Json.JsonValueKind.Null ? "none"
            : $"{e.GetProperty("price").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture)}@{(e.GetProperty("world").ValueKind == System.Text.Json.JsonValueKind.Null ? "-" : e.GetProperty("world").GetString())}";
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var handler = new CannedUniversalis(c);
            var universalis = new Universalis(new System.Net.Http.HttpClient(handler), "https://universalis.test");
            var exclude = c.GetProperty("retainers").EnumerateArray().Select(r => Universalis.RetainerKey(r.GetProperty("name").GetString()!, r.GetProperty("world").GetString())).ToHashSet();
            var listings = c.GetProperty("listings").EnumerateArray().Select(l => new ListingRow(long.Parse(l.GetProperty("retainer_id").GetString()!, System.Globalization.CultureInfo.InvariantCulture), 0,
                l.GetProperty("slot").GetInt32(), l.GetProperty("item_id").GetInt32(), l.GetProperty("quantity").GetInt32(), l.GetProperty("hq").GetBoolean(), l.GetProperty("unit_price").GetInt64(),
                null, l.GetProperty("retainer_name").GetString(), null, l.GetProperty("world").GetString(), null, null, 0, 0, false)).ToList();
            var ventures = c.GetProperty("ventures").EnumerateArray().Select(v => (Cid: v.GetProperty("cid").GetString()!, World: v.GetProperty("world").GetString()!,
                Items: v.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("item_id").GetInt32(), i.GetProperty("quantity").GetInt64(), i.GetProperty("hq_quantity").GetInt64())).ToList())).ToList();
            var vendor = c.GetProperty("vendor").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetDouble());
            var market = await universalis.MarketFor(listings.Select(l => (l.World, l.ItemId)).Concat(ventures.SelectMany(v => v.Items.Select(i => ((string?)v.World, i.Item1)))));
            var expected = c.GetProperty("expected");
            var names = expected.GetProperty("names").EnumerateArray().Select(n => $"{n.GetProperty("world").GetString()}:{n.GetProperty("names").GetProperty("dc").GetString() ?? "-"}:{n.GetProperty("names").GetProperty("region").GetString() ?? "-"}").ToList();
            var mineNames = market.Select(kv => $"{kv.Key}:{kv.Value.Names.Dc ?? "-"}:{kv.Value.Names.Region ?? "-"}").ToList();
            var compared = MarketMath.Compare(listings, market, exclude).Select(x => $"{x.Listing.RetainerId}|{x.Listing.Slot} {x.Undercut} {(x.UndercutBy?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-")} {Price(x.World)} {Price(x.Dc)} {Price(x.Region)}").ToList();
            var app = expected.GetProperty("listings").EnumerateArray().Select(x => $"{x.GetProperty("retainer_id").GetString()}|{x.GetProperty("slot").GetInt32()} {x.GetProperty("undercut").GetBoolean()} "
                + $"{(x.GetProperty("undercut_by").ValueKind == System.Text.Json.JsonValueKind.Null ? "-" : x.GetProperty("undercut_by").GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture))} "
                + $"{PriceJson(x.GetProperty("others").GetProperty("world"))} {PriceJson(x.GetProperty("others").GetProperty("dc"))} {PriceJson(x.GetProperty("others").GetProperty("region"))}").ToList();
            var firstListing = Enumerable.Range(0, Math.Max(compared.Count, app.Count)).FirstOrDefault(k => k >= compared.Count || k >= app.Count || compared[k] != app[k], -1);
            var ventureMine = ventures.Select(v => $"{v.Cid} " + string.Join(" ", Universalis.Scopes.Select(s => Math.Round(MarketMath.VentureGil(v.Items, market.GetValueOrDefault(v.World), s, vendor, exclude), 4).ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToList();
            var ventureApp = expected.GetProperty("ventures").EnumerateArray().Select(v => $"{v.GetProperty("cid").GetString()} " + string.Join(" ", Universalis.Scopes.Select(s => Math.Round(v.GetProperty("gil").GetProperty(s).GetDouble(), 4).ToString(System.Globalization.CultureInfo.InvariantCulture)))).ToList();
            var asked = handler.Asked;
            await universalis.MarketFor(listings.Select(l => (l.World, l.ItemId)));
            Check(names.SequenceEqual(mineNames) && firstListing < 0 && ventureMine.SequenceEqual(ventureApp) && handler.Asked == asked,
                $"market prices the same as the app's, {c.GetProperty("name").GetString()}: {compared.Count} listings ({compared.Count(x => x.Contains(" True "))} undercut), {ventureMine.Count} characters' ventures, asked again within 30 minutes: kept"
                + (firstListing >= 0 ? $" | listing {firstListing + 1}: plugin [{(firstListing < compared.Count ? compared[firstListing] : "none")}], app [{(firstListing < app.Count ? app[firstListing] : "none")}]" : "")
                + (ventureMine.SequenceEqual(ventureApp) ? "" : $" | ventures: plugin [{string.Join("; ", ventureMine)}], app [{string.Join("; ", ventureApp)}]")
                + (names.SequenceEqual(mineNames) ? "" : $" | places: plugin [{string.Join(", ", mineNames)}], app [{string.Join(", ", names)}]"));
        }
    }

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

    // Routes stage: the routes read through the database (stand-ins for the views with the PostgreSQL types they give:
    // ids numeric, a sub's stops bytea, times timestamptz), worked out at the fixture's moment: the app's answers.
    // The Shopping List's database part: what's held where, the ticks, and (with Live Market Prices on) the fleet's own
    // retainers and how many characters are on each world.
    private static async Task ShoppingReadFromTheDatabase(NpgsqlConnection conn)
    {
        await Exec(conn, @"DROP TABLE IF EXISTS fleet_shopping_held_view, fleet_shopping_progress_view, fleet_market_settings_view, fleet_retainers_view, fleet_parts_characters_view;
            CREATE TABLE fleet_shopping_held_view (item_id int, who text, place text, qty bigint);
            INSERT INTO fleet_shopping_held_view VALUES (31001, 'Char1 (Maduin)', 'Bags', 20), (31001, 'Company 7', 'FC chest', 999), (2, 'Char1 (Maduin)', 'Crystals', 5);
            CREATE TABLE fleet_shopping_progress_view (progress text);
            INSERT INTO fleet_shopping_progress_view VALUES ('{""24348"":{""made"":1,""outOf"":2}}');
            CREATE TABLE fleet_market_settings_view (stale_days int, live_prices boolean);
            INSERT INTO fleet_market_settings_view VALUES (7, false);
            CREATE TABLE fleet_retainers_view (retainer_name text, world text);
            INSERT INTO fleet_retainers_view VALUES ('Amy', 'Maduin'), ('', 'Golem');
            CREATE TABLE fleet_parts_characters_view (world text);
            INSERT INTO fleet_parts_characters_view VALUES ('Maduin'), ('Maduin'), ('Golem'), (NULL), ('');");
        var off = await FleetReader.ReadShopping(conn);
        Check(off.Held.Count == 3 && off.Held[0] == new HeldRow(2, "Char1 (Maduin)", "Crystals", 5) && off.Held.Sum(h => h.Qty) == 1024
              && off.Progress == "{\"24348\":{\"made\":1,\"outOf\":2}}" && !off.LivePrices && off.Exclude.Count == 0 && off.Worlds.Count == 0,
            "shopping: reads what's held where and the ticks; with Live Market Prices off, not the retainers or worlds");
        await Exec(conn, "UPDATE fleet_market_settings_view SET live_prices = true; DELETE FROM fleet_shopping_progress_view");
        var on = await FleetReader.ReadShopping(conn);
        Check(on.LivePrices && on.Progress == null && on.Exclude.SetEquals(new[] { Universalis.RetainerKey("Amy", "Maduin") })
              && on.Worlds.OrderBy(w => w.World).SequenceEqual(new[] { ("Golem", 1), ("Maduin", 2) }),
            "shopping: with Live Market Prices on, the fleet's own retainers (not unnamed ones) and the characters on each world");
        await Exec(conn, "DROP TABLE fleet_shopping_held_view, fleet_shopping_progress_view, fleet_market_settings_view, fleet_retainers_view, fleet_parts_characters_view");
    }

    // The Trends page: a made-up recorded history (in a schema of its own, with the app's tables) read with the plugin's
    // chart query, against the app's own answers for the same requests (tests/db/trends-fixture.js).
    private static async Task TrendsAgainstTheApp(NpgsqlConnection conn)
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "trends-fixture.json");
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var root = doc.RootElement;
        await Exec(conn, @"DROP SCHEMA IF EXISTS trends_check CASCADE; CREATE SCHEMA trends_check;
            CREATE TABLE trends_check.companion_metric_history (recorded_at timestamptz NOT NULL, subject_type text NOT NULL, subject_id numeric NOT NULL,
                metric text NOT NULL, value numeric NOT NULL, label text);
            CREATE INDEX ON trends_check.companion_metric_history (subject_type, subject_id, metric, recorded_at DESC);
            CREATE TABLE trends_check.companion_metric_latest (subject_type text NOT NULL, subject_id numeric NOT NULL, metric text NOT NULL, value numeric NOT NULL,
                recorded_at timestamptz NOT NULL, label text, PRIMARY KEY (subject_type, subject_id, metric));
            CREATE TABLE trends_check.companion_character_current (cid numeric PRIMARY KEY, name text, world text, account_label text, gil bigint, ceruleum int,
                repair_kits int, retainer_count int, submarine_count int, num_sub_slots int, fc_id numeric, first_seen_at timestamptz, last_synced_at timestamptz);
            CREATE TABLE trends_check.character_sort_order (cid numeric PRIMARY KEY, sort_index int);
            CREATE TABLE trends_check.companion_character_housing (cid numeric PRIMARY KEY, fc_id numeric, fc_name text, fc_master text, updated_at timestamptz);");
        var times = new HashSet<string> { "recorded_at", "first_seen_at", "last_synced_at", "updated_at" };
        foreach (var table in root.GetProperty("tables").EnumerateObject())
            foreach (var row in table.Value.EnumerateArray())
            {
                var cols = row.EnumerateObject().ToList();
                await using var cmd = new NpgsqlCommand($"INSERT INTO trends_check.{table.Name} ({string.Join(", ", cols.Select(c => c.Name))}) VALUES ("
                    + string.Join(", ", cols.Select((c, i) => times.Contains(c.Name) ? $"to_timestamp(CAST(@p{i} AS float8) / 1000)" : $"@p{i}")) + ")", conn);
                for (var i = 0; i < cols.Count; i++)
                {
                    var v = cols[i].Value;
                    object value = v.ValueKind switch
                    {
                        JsonValueKind.Null => DBNull.Value,
                        JsonValueKind.String when cols[i].Name is "cid" or "fc_id" or "subject_id" => decimal.Parse(v.GetString()!, System.Globalization.CultureInfo.InvariantCulture),
                        JsonValueKind.String => v.GetString()!,
                        _ when times.Contains(cols[i].Name) => v.GetDouble(),
                        _ when cols[i].Name is "value" => v.GetDecimal(),
                        _ => v.GetInt64(),
                    };
                    cmd.Parameters.AddWithValue($"p{i}", value);
                }
                await cmd.ExecuteNonQueryAsync();
            }
        var builder = new NpgsqlConnectionStringBuilder(conn.ConnectionString) { SearchPath = "trends_check" };
        if (Environment.GetEnvironmentVariable("FLEET_TEST_DB") is { } given) builder = new NpgsqlConnectionStringBuilder(given) { SearchPath = "trends_check" };
        await using var trends = new NpgsqlConnection(builder.ConnectionString);
        await trends.OpenAsync();

        // What's recorded, and the characters and Free Companies to pick from.
        var meta = await FleetReader.ReadTrendsMeta(trends);
        var appCatalog = root.GetProperty("catalog").GetProperty("rows").EnumerateArray()
            .Select(r => $"{r.GetProperty("metric").GetString()} {r.GetProperty("subjectType").GetString()} {r.GetProperty("subjects").GetInt32()} {r.GetProperty("firstRecorded").GetDouble()} {r.GetProperty("lastRecorded").GetDouble()}").ToList();
        var myCatalog = meta.Catalog.Select(r => $"{r.Metric} {r.SubjectType} {r.Subjects} {r.FirstRecorded} {r.LastRecorded}").ToList();
        Check(myCatalog.SequenceEqual(appCatalog), $"trends: what's recorded, as the app's catalog ({appCatalog.Count} values)"
            + (myCatalog.SequenceEqual(appCatalog) ? "" : $"\n   plugin {string.Join("; ", myCatalog.Take(3))}\n   app    {string.Join("; ", appCatalog.Take(3))}"));
        static string? S(JsonElement e, string k) => e.GetProperty(k).ValueKind == JsonValueKind.Null ? null : e.GetProperty(k).ValueKind == JsonValueKind.String ? e.GetProperty(k).GetString() : e.GetProperty(k).GetRawText();
        var subjects = root.GetProperty("subjects");
        var appChars = subjects.GetProperty("characters").EnumerateArray().Select(c => $"{S(c, "id")} {S(c, "name")} {S(c, "world")} {S(c, "accountLabel")} {S(c, "sortIndex")}").ToList();
        var myChars = meta.Characters.Select(c => $"{c.Id} {c.Name} {c.World} {c.Account} {c.SortIndex}").ToList();
        var appFcs = subjects.GetProperty("fcs").EnumerateArray().Select(f => $"{S(f, "id")} {S(f, "name")} {S(f, "leader")}").ToList();
        var myFcs = meta.Fcs.Select(f => $"{f.Id} {f.Name} {f.Leader}").ToList();
        Check(myChars.SequenceEqual(appChars) && myFcs.SequenceEqual(appFcs), $"trends: the characters (in Roster order) and Free Companies to pick from, as the app's ({myChars.Count} and {myFcs.Count})"
            + (myChars.SequenceEqual(appChars) && myFcs.SequenceEqual(appFcs) ? "" : $"\n   plugin {string.Join("; ", myChars)} | {string.Join("; ", myFcs)}\n   app    {string.Join("; ", appChars)} | {string.Join("; ", appFcs)}"));

        // Every chart the fixture asked the app for, asked the same way here.
        foreach (var asked in root.GetProperty("series").EnumerateArray())
        {
            var name = asked.GetProperty("name").GetString();
            var q = asked.GetProperty("request");
            var parts = q.GetProperty("parts").EnumerateArray().Select(p => new TrendPart(p.GetProperty("metric").GetString()!, p.GetProperty("subjectType").GetString()!,
                p.TryGetProperty("weight", out var w) ? w.GetDouble() : 1)).ToList();
            var ids = q.TryGetProperty("subjectIds", out var given2) ? given2.EnumerateArray().Select(x => x.GetString()!).ToList() : null;
            var (request, error) = TrendsMath.Check(DateTimeOffset.Parse(q.GetProperty("from").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(),
                DateTimeOffset.Parse(q.GetProperty("to").GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToUnixTimeMilliseconds(), q.GetProperty("points").GetInt32(), parts, ids);
            var answer = asked.GetProperty("answer");
            if (!answer.GetProperty("success").GetBoolean())
            {
                Check(request == null && error == answer.GetProperty("error").GetString(), $"trends: refused as the app refuses it, {name}: {error}");
                continue;
            }
            var result = await FleetReader.ReadSeries(trends, request!);
            var app = answer.GetProperty("timestamps").EnumerateArray().Select(t => t.GetDouble()).ToList();
            var bad = !result.Timestamps.SequenceEqual(app) ? $"times: plugin {result.Timestamps.FirstOrDefault()}..{result.Timestamps.LastOrDefault()}, app {app.FirstOrDefault()}..{app.LastOrDefault()}" : null;
            var appSeries = answer.GetProperty("series").EnumerateArray().ToList();
            if (bad == null && appSeries.Count != result.Series.Count) bad = $"{result.Series.Count} lines, app {appSeries.Count}";
            for (var k = 0; bad == null && k < appSeries.Count; k++)
            {
                var a = appSeries[k];
                var mine = result.Series[k];
                if (S(a, "subjectId") != mine.SubjectId) { bad = $"line {k}: plugin {mine.SubjectId}, app {S(a, "subjectId")}"; break; }
                var values = a.GetProperty("values").EnumerateArray().Select(v => v.ValueKind == JsonValueKind.Null ? (double?)null : v.GetDouble()).ToList();
                var at = Enumerable.Range(0, values.Count).FirstOrDefault(i => values[i] != mine.Values[i], -1);
                if (values.Count != mine.Values.Count || at >= 0) bad = $"line {k} point {at}: plugin {mine.Values.ElementAtOrDefault(at)}, app {values.ElementAtOrDefault(at)}";
            }
            Check(bad == null, $"trends: the chart the same as the app's, {name} ({result.Series.Sum(s => s.Values.Count(v => v != null))} values)" + (bad == null ? "" : $"\n   {bad}"));
        }
        await Exec(conn, "DROP SCHEMA trends_check CASCADE");
    }

    // The Trends page's own working out: the numbers under a chart, the axis and the short numbers.
    private static void TrendsMathChecks()
    {
        var day = TrendsMath.Day;
        var stats = TrendsMath.Stats(new double?[] { null, 100, 150, null, 90, 130 }, new double[] { 0, day, 2 * day, 3 * day, 4 * day, 5 * day });
        Check(stats == new TrendStats(130, 100, 30, 30, 7.5, 90, 150),
            "trends: under a chart, now, at the start (its first recorded point), the change and its share, a day on average (30 over 4 days), lowest and highest: " + stats);
        var flat = TrendsMath.Stats(new double?[] { 0, 0, 5 }, new double[] { 0, day / 2, day });
        Check(flat.Percent == null && flat.PerDay == 5 && flat.Change == 5, "trends: from nothing, no share of the start is given (it would be infinite)");
        var none = TrendsMath.Stats(new double?[] { null, null }, new double[] { 0, day });
        var one = TrendsMath.Stats(new double?[] { null, 7 }, new double[] { 0, day });
        Check(none.Current == null && one.PerDay == null && one.Change == 0, "trends: nothing recorded gives nothing; one point gives no rate a day");
        Check(TrendsMath.Compact(1_250_000) == "1.25M" && TrendsMath.Compact(-980_400) == "-980.4K" && TrendsMath.Compact(12.345) == "12.35" && TrendsMath.Compact(3e9) == "3B"
              && TrendsMath.Full(1234567.5) == "1,234,568" && TrendsMath.Full(null) == "—",
            "trends: short numbers for the axis and whole numbers with separators, as the app's");
        var ticks = TrendsMath.NiceTicks(-3, 104);
        Check(ticks.SequenceEqual(new double[] { 0, 25, 50, 75, 100 }) && TrendsMath.NiceTicks(5, 5).Count == 1,
            "trends: the axis has round values (0, 25, 50, 75, 100 for -3 to 104): " + string.Join(",", ticks));
        Check(TrendsMath.Range(new double?[] { 100, null, 200 }) == (92, 208) && TrendsMath.Range(new double?[] { 5, 5 }) == (4, 6) && TrendsMath.Range(new double?[] { null }) == (0, 1),
            "trends: the chart's range has room above and below (8%), as the app's");
        var (rounded, _) = TrendsMath.Check(90_061_234, 90_061_234 + 3600_000, 50, new() { new TrendPart("gil", "character") }, null);
        var (exact, _) = TrendsMath.Check(90_061_234, 90_061_234 + 600_000, 50, new() { new TrendPart("gil", "character") }, null);
        Check(rounded!.From == 90_060_000 && rounded.To == 93_720_000 && exact!.From == 90_061_234,
            "trends: spans of half an hour or more go to whole minutes (the end rounded up); a 10-minute zoom stays exact");
        Check(TrendsMath.Check(0, 1, 50, new() { new TrendPart("gil; drop", "character") }, null).Error == "Invalid metric name."
              && TrendsMath.Check(0, 1, 50, new() { new TrendPart("gil", "retainer") }, null).Error == "Invalid subject type."
              && TrendsMath.Check(0, 1, 50, new() { new TrendPart("gil", "character") }, new() { "12a" }).Error == "Invalid subject id.",
            "trends: a request the app would refuse is refused the same way");
    }

    private static async Task RoutesReadFromTheDatabase(NpgsqlConnection conn)
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "routes-fixture.json");
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var game = GameFrom(doc.RootElement.GetProperty("game"));
        var first = doc.RootElement.GetProperty("cases")[0];
        var tables = new (string View, string Key, string Columns)[]
        {
            ("fleet_parts_characters_view", "characters", "cid numeric, name text, world text, account_label text, sort_index int, fc_id text, num_sub_slots int, has_house boolean"),
            ("fleet_parts_subs_view", "subs", "cid numeric, sub_name text, slot int, rank int, part1 int, part2 int, part3 int, part4 int, craft_enabled boolean"),
            ("fleet_parts_rules_view", "rules", "id int, rank int, cid_override numeric, sub_filter text, hull text, stern text, bow text, bridge text"),
            ("fleet_parts_fight_club_view", "fightClub", "id int, route text, hours numeric, repair_times text, gil_sub_day numeric, rec_build_notes text, repair_kit_cost numeric"),
            ("fleet_parts_spares_view", null!, "cid numeric, fc_id text, is_retainer boolean, retainer_id numeric, retainer_name text, sorted_container int, sorted_slot_index int, item_id int, quantity int"),
            ("fleet_route_subs_view", "subs", "cid numeric, sub_name text, slot int, rank int, part1 int, part2 int, part3 int, part4 int, points bytea, return_time bigint, "
                + "current_exp bigint, next_level_exp bigint, craft_enabled boolean, fc_id text"),
            ("fleet_route_fight_club_view", "fightClub", "id int, route text, hours numeric, repair_times text, gil_sub_day numeric, rec_build_notes text, unmod_min_rank text, "
                + "min_rank_build text, repair_kit_cost numeric"),
            ("fleet_sub_history_view", "history", "cid numeric, metric text, recorded_at timestamptz, value numeric"),
            ("fleet_voyage_sectors_view", "voyages", "fc_id text, sub_register bigint, sub_name text, returned_at timestamptz, sector int, leg int"),
        };
        foreach (var (view, key, columns) in tables)
        {
            await Exec(conn, $"DROP TABLE IF EXISTS {view}; CREATE TABLE {view} ({columns})");
            if (key == null) continue;
            var cols = columns.Split(", ").Select(c => (Name: c.Split(' ')[0], Type: c.Split(' ')[1])).ToList();
            foreach (var row in first.GetProperty("inputs").GetProperty(key).EnumerateArray())
            {
                // As stored: a sub's stops as bytes, times from milliseconds.
                string Value(int i) => cols[i].Type switch
                {
                    "bytea" => $"decode(@p{i}, 'hex')",
                    "timestamptz" => $"to_timestamp(CAST(@p{i} AS float8) / 1000)",
                    var type => $"CAST(@p{i} AS {type})",
                };
                await using var cmd = new NpgsqlCommand($"INSERT INTO {view} ({string.Join(", ", cols.Select(c => c.Name))}) VALUES ({string.Join(", ", cols.Select((_, i) => Value(i)))})", conn);
                for (var i = 0; i < cols.Count; i++)
                {
                    var v = row.TryGetProperty(cols[i].Name, out var found) ? found : default;
                    cmd.Parameters.AddWithValue($"p{i}", v.ValueKind switch
                    {
                        System.Text.Json.JsonValueKind.Undefined or System.Text.Json.JsonValueKind.Null => DBNull.Value,
                        System.Text.Json.JsonValueKind.True => "true",
                        System.Text.Json.JsonValueKind.False => "false",
                        System.Text.Json.JsonValueKind.String => v.GetString()!,
                        System.Text.Json.JsonValueKind.Array => Convert.ToHexString(v.EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray()),
                        _ => v.GetRawText(),
                    });
                }
                await cmd.ExecuteNonQueryAsync();
            }
        }
        var read = await FleetReader.ReadRoutes(conn, game, first.GetProperty("now").GetDouble());
        var mine = read.Subs.OrderBy(s => s.Cid).ThenBy(s => s.SubName, StringComparer.Ordinal).Select(RouteLine).ToList();
        var app = first.GetProperty("expected").EnumerateArray()
            .OrderBy(e => long.Parse(e.GetProperty("cid").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
            .ThenBy(e => e.GetProperty("sub_name").GetString(), StringComparer.Ordinal).Select(AppRouteLine).ToList();
        var bad = Enumerable.Range(0, Math.Max(mine.Count, app.Count)).FirstOrDefault(k => k >= mine.Count || k >= app.Count || mine[k] != app[k], -1);
        Check(bad < 0 && mine.Count > 0 && read.Subs.Any(s => s.CurrentRoute.Length > 0) && read.Subs.Any(s => s.RankUp.Method == "voyages"),
            $"routes: read through the database's views (a sub's stops as bytes, times as timestamps), the app's answers ({mine.Count} subs)"
            + (bad < 0 ? "" : $"\n   plugin [{(bad < mine.Count ? mine[bad] : "none")}]\n   app    [{(bad < app.Count ? app[bad] : "none")}]"));
        try
        {
            await FleetReader.ReadRoutes(conn, null, 0);
            Check(false, "routes: without the game's sea map data, the read says so");
        }
        catch (InvalidOperationException ex)
        {
            Check(ex.Message.Contains("sea map", StringComparison.Ordinal), $"routes: without the game's sea map data, the read says so ({ex.Message})");
        }
        await Exec(conn, string.Join("; ", tables.Select(t => $"DROP TABLE {t.View}")));
    }

    // Batch 4: the Parts page's working out (PartsPlanner) against the app's own answers for the same made-up fleets
    // (written by the app's tests/db/parts-fixture.js from its fleet_parts_* views and computePartsNeeded).
    // Routes stage: each sub's route, gil a day, speed and range, rank-up dates and next build, worked out here
    // (SubRoutes) from the app's made-up fleets and game data, against the app's own answers (routes-fixture.json).
    internal static SubGameData GameFrom(System.Text.Json.JsonElement g) => new(
        g.GetProperty("sectors").EnumerateArray().Select(e => new SubSector(e.GetProperty("id").GetInt32(), e.GetProperty("location").GetString() ?? "",
            e.GetProperty("map").GetInt32(), e.GetProperty("x").GetDouble(), e.GetProperty("y").GetDouble(), e.GetProperty("z").GetDouble(),
            e.GetProperty("expReward").GetDouble(), e.GetProperty("surveyMinutes").GetDouble(), e.GetProperty("starting").GetBoolean())).ToList(),
        g.GetProperty("maps").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetInt32(), e => e.GetProperty("name").GetString() ?? ""),
        g.GetProperty("ranks").EnumerateArray().Select(e => new SubRank(e.GetProperty("id").GetInt32(), e.GetProperty("speed").GetDouble(),
            e.GetProperty("range").GetDouble(), e.GetProperty("expToNext").GetDouble())).ToList(),
        g.GetProperty("parts").EnumerateArray().Select(e => new SubPartStat(e.GetProperty("class").GetInt32(), e.GetProperty("slot").GetInt32(),
            e.GetProperty("speed").GetDouble(), e.GetProperty("range").GetDouble())).ToList());

    internal static RoutesInputs RoutesInputsFrom(System.Text.Json.JsonElement i)
    {
        static string? S(System.Text.Json.JsonElement e, string k) => e.GetProperty(k).ValueKind == System.Text.Json.JsonValueKind.Null ? null
            : e.GetProperty(k).ValueKind == System.Text.Json.JsonValueKind.String ? e.GetProperty(k).GetString() : e.GetProperty(k).GetRawText();
        static long L(System.Text.Json.JsonElement e, string k) => long.Parse(S(e, k)!, System.Globalization.CultureInfo.InvariantCulture);
        static long? LN(System.Text.Json.JsonElement e, string k) => S(e, k) is { } t ? (long)double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
        static int? IN(System.Text.Json.JsonElement e, string k) => S(e, k) is { } t ? (int)double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
        static double? DN(System.Text.Json.JsonElement e, string k) => S(e, k) is { } t ? double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
        static bool B(System.Text.Json.JsonElement e, string k) => e.GetProperty(k).ValueKind == System.Text.Json.JsonValueKind.True;
        return new RoutesInputs(
            i.GetProperty("characters").EnumerateArray().Select(e => new PartsCharacter(L(e, "cid"), S(e, "name") ?? "", S(e, "world") ?? "", S(e, "account_label") ?? "",
                IN(e, "sort_index"), S(e, "fc_id"), IN(e, "num_sub_slots") ?? 0, B(e, "has_house"))).ToList(),
            i.GetProperty("subs").EnumerateArray().Select(e => new RouteSub(L(e, "cid"), S(e, "sub_name") ?? "", IN(e, "slot"), IN(e, "rank") ?? 0,
                IN(e, "part1"), IN(e, "part2"), IN(e, "part3"), IN(e, "part4"),
                e.GetProperty("points").ValueKind == System.Text.Json.JsonValueKind.Array ? e.GetProperty("points").EnumerateArray().Select(b => (byte)b.GetInt32()).ToArray() : null,
                LN(e, "return_time"), LN(e, "current_exp"), LN(e, "next_level_exp"), B(e, "craft_enabled"), S(e, "fc_id"))).ToList(),
            i.GetProperty("rules").EnumerateArray().Select(e => new PartsRule(L(e, "id"), IN(e, "rank") ?? 0, S(e, "cid_override"), S(e, "sub_filter"),
                S(e, "hull"), S(e, "stern"), S(e, "bow"), S(e, "bridge"))).ToList(),
            i.GetProperty("fightClub").EnumerateArray().Select(e => new FightClubRow(L(e, "id"), S(e, "route") ?? "", DN(e, "hours"), S(e, "repair_times"),
                DN(e, "gil_sub_day"), S(e, "rec_build_notes"), S(e, "unmod_min_rank"), S(e, "min_rank_build"), DN(e, "repair_kit_cost") ?? 0)).ToList(),
            i.GetProperty("history").EnumerateArray().Select(e => new SubHistoryPoint(L(e, "cid"), S(e, "metric") ?? "", DN(e, "recorded_at") ?? 0, DN(e, "value") ?? 0)).ToList(),
            i.GetProperty("voyages").EnumerateArray().Select(e => new VoyageSector(S(e, "fc_id") ?? "", L(e, "sub_register"), S(e, "sub_name"), DN(e, "returned_at") ?? 0,
                IN(e, "sector") ?? 0, IN(e, "leg"))).ToList());
    }

    // One sub's answers as one line, the plugin's and the app's written alike (numbers to 9 significant figures, dates to
    // the millisecond).
    private static string N(double? v) => v is double d ? d.ToString("G9", System.Globalization.CultureInfo.InvariantCulture) : "-";
    private static string RouteLine(SubRoute s)
    {
        var r = s.RankUp;
        var b = r.NextBuild;
        return string.Join(" | ", s.Cid, s.SubName, s.CurrentSetup, s.CurrentRoute, s.CurrentMap?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "-",
            s.TargetRoute, N(s.TargetGilDay), N(s.CurrentGilDay), N(s.Speed), N(s.Range), N(s.RankProgressPct),
            r.Method ?? "-", r.OutAtSea, N(r.PerDay), r.Voyage is { } v ? $"{N(v.Exp)}/{N(v.Hours)}" : "-", r.Bonus is { } bo ? $"{N(bo.Factor)}x{bo.Voyages}" : "-",
            r.BuildAhead, r.MaxRank, N(r.NextRank), N(r.NextRankAt is double a ? Math.Round(a) : null), N(r.Voyages),
            b == null ? "-" : $"{b.Rank} {b.Build} [{string.Join(",", b.Parts)}] {N(b.At is double ba ? Math.Round(ba) : null)} {N(b.Voyages)} "
                + (b.FightClub is { } f ? $"{f.Route} {N(f.Net)} {N(f.Gain)}" : "-"));
    }

    private static string AppRouteLine(System.Text.Json.JsonElement e)
    {
        static double? D(System.Text.Json.JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.Number ? v.GetDouble() : null;
        static string? T(System.Text.Json.JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String ? v.GetString() : null;
        static bool B(System.Text.Json.JsonElement x, string k) => x.TryGetProperty(k, out var v) && v.ValueKind == System.Text.Json.JsonValueKind.True;
        using var doc = System.Text.Json.JsonDocument.Parse(e.GetProperty("rank_up").GetString()!);
        var r = doc.RootElement;
        var voyage = r.GetProperty("voyage");
        var bonus = r.GetProperty("bonus");
        var b = r.GetProperty("nextBuild");
        string build = "-";
        if (b.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            var f = b.GetProperty("fightClub");
            build = $"{D(b, "rank")} {T(b, "build")} [{string.Join(",", b.GetProperty("parts").EnumerateArray().Select(p => p.GetString()))}] {N(D(b, "at"))} {N(D(b, "voyages"))} "
                + (f.ValueKind == System.Text.Json.JsonValueKind.Object ? $"{T(f, "route")} {N(D(f, "net"))} {N(D(f, "gain"))}" : "-");
        }
        return string.Join(" | ", T(e, "cid"), T(e, "sub_name"), T(e, "current_setup"), T(e, "current_route"), N(D(e, "current_map")),
            T(e, "target_route"), N(D(e, "target_gil_day")), N(D(e, "current_gil_day")), N(D(e, "speed")), N(D(e, "range")), N(D(e, "rank_progress_pct")),
            T(r, "method") ?? "-", B(r, "outAtSea"), N(D(r, "perDay")), voyage.ValueKind == System.Text.Json.JsonValueKind.Object ? $"{N(D(voyage, "exp"))}/{N(D(voyage, "hours"))}" : "-",
            bonus.ValueKind == System.Text.Json.JsonValueKind.Object ? $"{N(D(bonus, "factor"))}x{D(bonus, "voyages")}" : "-",
            B(r, "buildAhead"), D(r, "maxRank"), N(D(r, "nextRank")), N(D(r, "nextRankAt")), N(D(r, "voyages")), build);
    }

    // The in-game edits: the app's database (db/schema.sql) in a database of its own, the same starting rows, then each
    // edit the app made, made the plugin's way; the answer and every edited table must come out the same each time.
    private static async Task EditsAgainstTheApp(NpgsqlConnection conn)
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "edits-fixture.json");
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var root = doc.RootElement;
        await Exec(conn, "DROP DATABASE IF EXISTS fleet_edits_plugin");
        await Exec(conn, "CREATE DATABASE fleet_edits_plugin");
        var given = Environment.GetEnvironmentVariable("FLEET_TEST_DB") ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";
        await using (var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(given) { Database = "fleet_edits_plugin", Pooling = false }.ConnectionString))
        {
            await db.OpenAsync();
            await Exec(db, root.GetProperty("schema").GetString()!);
            await Exec(db, root.GetProperty("seed").GetString()!);
            var tables = root.GetProperty("tables").EnumerateObject().Select(t => (t.Name, Sql: t.Value.GetString()!)).ToList();
            async Task<string> Tables()
            {
                var all = new JsonObject();
                foreach (var (name, sql) in tables)
                {
                    var rows = new JsonArray();
                    await using var cmd = new NpgsqlCommand(sql, db);
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                    {
                        var row = new JsonObject();
                        for (var i = 0; i < r.FieldCount; i++)
                            row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i) switch
                            {
                                bool b => JsonValue.Create(b),
                                int n => JsonValue.Create(n),
                                long n => JsonValue.Create(n),
                                var v => JsonValue.Create(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)),
                            };
                        rows.Add(row);
                    }
                    all[name] = rows;
                }
                return all.ToJsonString();
            }
            using (var start = JsonDocument.Parse(await Tables()))
                Check(JsonDiff(start.RootElement, root.GetProperty("start"), "") == null, "edits: the same starting rows as the app's");

            static string? S(JsonElement e, string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
            static double D(JsonElement e) => e.ValueKind == JsonValueKind.Number ? e.GetDouble() : double.NaN;
            foreach (var edit in root.GetProperty("edits").EnumerateArray())
            {
                var call = edit.GetProperty("call").GetString()!;
                var args = edit.GetProperty("args").EnumerateArray().ToList();
                var a = args.FirstOrDefault();
                FleetEdits.Rule Rule(JsonElement r) => new(r.TryGetProperty("id", out var id) ? id.GetInt64() : null, r.GetProperty("rank").GetInt32(), S(r, "cid_override"),
                    S(r, "sub_filter"), S(r, "hull"), S(r, "stern"), S(r, "bow"), S(r, "bridge"));
                var answer = new JsonObject { ["success"] = true };
                string? error = null;
                switch (call)
                {
                    case "add-sub-build-rule": answer["id"] = await FleetEdits.AddRule(db, Rule(a)); break;
                    case "update-sub-build-rule": await FleetEdits.UpdateRule(db, Rule(a)); break;
                    case "delete-sub-build-rule": await FleetEdits.DeleteRule(db, a.GetInt64()); break;
                    case "set-craft-enabled": await FleetEdits.SetCraft(db, S(a, "cid")!, S(a, "subName")!, a.GetProperty("enabled").GetBoolean()); break;
                    case "save-retainer-target":
                        error = await FleetEdits.SaveTarget(db, S(a, "retainerId")!, S(a, "targetMode"),
                            a.GetProperty("targetValue").ValueKind == JsonValueKind.Number ? a.GetProperty("targetValue").GetInt32() : null, a.GetProperty("onlyCrafted").GetBoolean());
                        break;
                    case "delete-retainer-target": error = await FleetEdits.DeleteTarget(db, a.GetString()!); break;
                    case "update-sort-order": await FleetEdits.SaveOrder(db, a.EnumerateArray().Select(x => x.GetString()!).ToList()); break;
                    case "save-market-settings": error = await FleetEdits.SaveStaleDays(db, D(a.GetProperty("minDays"))); break;
                    case "save-income-settings": error = await FleetEdits.SaveIncomeWindow(db, a.GetProperty("windowDays").GetInt32()); break;
                    case "remove-pc":
                        var (removed, problem) = await FleetEdits.RemovePc(db, a.GetString()!, args[1].GetString());
                        error = problem;
                        if (problem == null) answer["removed"] = removed;
                        break;
                    case "save-shopping-progress":
                        error = await FleetEdits.SaveShoppingProgress(db, D(a.GetProperty("itemId")), D(a.GetProperty("made")), D(a.GetProperty("crafts")), a.GetProperty("aheadDays").GetInt32());
                        break;
                    case "settings:save-low-thresholds":
                        error = await FleetEdits.SaveWarnings(db, D(a.GetProperty("repairKitThreshold")), D(a.GetProperty("ceruleumThreshold")),
                            D(a.GetProperty("houseWarnDays")), D(a.GetProperty("notSyncedMinutes")));
                        break;
                    case "settings:save-repair-kit-cost": error = await FleetEdits.SaveRepairKitCost(db, D(a)); break;
                    case "save-universalis-setting": await FleetEdits.SaveLivePrices(db, a.GetBoolean()); break;
                    case "save-play-style": error = await FleetEdits.SavePlayStyle(db, a.GetString()!); break;
                    default: Check(false, $"edits: the plugin makes the app's {call}"); continue;
                }
                if (error != null) answer = new JsonObject { ["success"] = false, ["error"] = error };
                using var mine = JsonDocument.Parse(answer.ToJsonString());
                using var after = JsonDocument.Parse(await Tables());
                var diff = JsonDiff(mine.RootElement, edit.GetProperty("answer"), "answer") ?? JsonDiff(after.RootElement, edit.GetProperty("after"), "tables");
                Check(diff == null, $"edits: {call} {string.Join(" ", args.Select(x => x.GetRawText()))} leaves the database as the app's does" + (diff == null ? "" : $"\n   {diff}"));
            }
            // The shared settings as the plugin reads them (fleet_settings_view) after those edits, as the app's Settings reads them.
            await Exec(db, root.GetProperty("views").GetString()!);
            await Exec(db, "INSERT INTO companion_submarine_snapshot (cid, sub_name, level) VALUES (18014498578000001, 'Sub1', 50), (18014498578000002, 'Sub1', 0)");
            await Exec(db, "UPDATE companion_retainer_lookup SET job = 3 WHERE retainer_id = 33777097243660301");
            var st = await FleetReader.ReadSettings(db);
            var want = root.GetProperty("settings");
            var cost = want.GetProperty("repairKitCost");
            Check(st.RepairKitThreshold == want.GetProperty("repairKitThreshold").GetDouble() && st.CeruleumThreshold == want.GetProperty("ceruleumThreshold").GetDouble()
                  && st.HouseWarnDays == want.GetProperty("houseWarnDays").GetInt32() && st.NotSyncedMinutes == want.GetProperty("notSyncedMinutes").GetInt32()
                  && st.RepairKitCost == (cost.ValueKind == JsonValueKind.Number ? cost.GetDouble() : null) && st.LivePrices == want.GetProperty("livePrices").GetBoolean()
                  && st.PlayChoice == want.GetProperty("playChoice").GetString() && st.PlayStyle == want.GetProperty("playStyle").GetString()
                  && st.SubsBuilt == want.GetProperty("subs").GetInt32() && st.RetainersWithClass == want.GetProperty("retainers").GetInt32(),
                $"settings: read as the app's Settings reads them: {st} vs {want.GetRawText()}");

            // A PC whose plugin only reads checks in (database update 16), and shows on the PCs page as that.
            Check(await DetailsWriter.WritePcReadAsync(db, "READER", "", "2.0.0.40"), "PCs: a PC that only reads checks in");
            Check(await DetailsWriter.WritePcReadAsync(db, "DESKTOP-1", "A", "2.0.0.40"), "PCs: a PC that syncs checks in too");
            var pcs = await FleetReader.ReadPcs(db);
            var readerRow = pcs.Single(p => p.Pc == "READER");
            var synced = pcs.Single(p => p.Pc == "DESKTOP-1");
            Check(readerRow.LastSync == null && readerRow.LastRead is { } seen && Math.Abs((DateTime.Now - seen).TotalMinutes) < 1 && readerRow.PluginVersion == "2.0.0.40"
                  && synced.PluginVersion == "2.0.0.40" && synced.LastRead != null,
                $"PCs: the check-in is read back as when it last read: {readerRow}");
            var login = await new NpgsqlCommand("SELECT db_user FROM companion_pc_status WHERE pc_name = 'READER'", db).ExecuteScalarAsync();
            Check(login is string { Length: > 0 }, "PCs: the check-in records the login it uses, for a move's checks");
            await Exec(db, "DROP VIEW fleet_pcs_view; ALTER TABLE companion_pc_status DROP COLUMN last_read_at");
            Check(!await DetailsWriter.WritePcReadAsync(db, "READER", "", "2.0.0.40"), "PCs: a database without update 16 yet is left as it is");
        }
        // What do you run? worked out as the app's shared/play-style.js does.
        static string Style(string choice, int subs, int retainers) => new FleetSettings(0, 0, 7, 30, null, false, choice, subs, retainers).PlayStyle;
        Check(Style("auto", 3, 0) == "subs" && Style("auto", 0, 2) == "retainers" && Style("auto", 3, 2) == "both" && Style("auto", 0, 0) == "both"
              && Style("subs", 0, 9) == "subs" && Style("retainers", 9, 0) == "retainers" && Style("both", 9, 0) == "both" && Style("x", 3, 0) == "subs",
            "settings: What do you run? as the app works it out");
        await Exec(conn, "DROP DATABASE fleet_edits_plugin");
        Check(FleetEdits.SetProgress("{\"5\":3,\"x\":{\"made\":1},\"2\":{\"made\":1,\"outOf\":2}}", 7, 1, 2, 3) == "{\"2\":{\"made\":1,\"outOf\":2},\"5\":3,\"7\":{\"made\":1,\"outOf\":2,\"ahead\":3},\"x\":{\"made\":1}}",
            "edits: ticking keeps every other tick as it was, in the order the app reads them: " + FleetEdits.SetProgress("{\"5\":3,\"x\":{\"made\":1},\"2\":{\"made\":1,\"outOf\":2}}", 7, 1, 2, 3));
    }

    // The fixture's made-up crafting data as the plugin reads the game's sheets.
    private static ShoppingGame ShopGameFrom(System.Text.Json.JsonElement shop)
    {
        static List<int> Ints(System.Text.Json.JsonElement e) => e.EnumerateArray().Select(x => x.GetInt32()).ToList();
        var items = shop.GetProperty("items").EnumerateArray().ToDictionary(e => e.GetProperty("id").GetInt32(),
            e => (Name: e.GetProperty("name").ValueKind == System.Text.Json.JsonValueKind.String ? e.GetProperty("name").GetString() : null, Price: e.GetProperty("priceMid").GetDouble()));
        var vendor = new Dictionary<int, double>();
        foreach (var id in Ints(shop.GetProperty("gilShop"))) if (items.TryGetValue(id, out var it)) vendor[id] = it.Price;
        return new ShoppingGame(
            shop.GetProperty("recipes").EnumerateArray().Select(r => new ShopRecipeRow(r.GetProperty("id").GetInt32(), r.GetProperty("result").GetInt32(), r.GetProperty("yield").GetInt32(),
                r.GetProperty("craftType").GetInt32(), r.GetProperty("ingredients").EnumerateArray().Select(x => (x[0].GetInt32(), x[1].GetInt32())).ToList())),
            shop.GetProperty("sequences").EnumerateArray().Select(q => new ShopSequence(q.GetProperty("id").GetInt32(), q.GetProperty("result").GetInt32(), Ints(q.GetProperty("parts")))),
            shop.GetProperty("craftParts").EnumerateArray().Select(c => (c.GetProperty("id").GetInt32(), Ints(c.GetProperty("procs")))),
            shop.GetProperty("processes").EnumerateArray().Select(p => new ShopProcess(p.GetProperty("id").GetInt32(), Ints(p.GetProperty("supply")), Ints(p.GetProperty("perSet")), Ints(p.GetProperty("sets")))),
            shop.GetProperty("supplyItems").EnumerateArray().Select(x => (x.GetProperty("id").GetInt32(), x.GetProperty("item").GetInt32())),
            vendor, id => items.TryGetValue(id, out var it) ? it.Name : null);
    }

    // The fixture's made-up Universalis: its places, and the listings it gave the app for each place and item.
    private sealed class FixtureMarket : IShoppingMarket
    {
        private readonly Dictionary<string, (string Name, string Dc, string Region)> places = new();
        private readonly System.Text.Json.JsonElement listings;
        public FixtureMarket(System.Text.Json.JsonElement placeList, System.Text.Json.JsonElement listings)
        {
            foreach (var dc in placeList.EnumerateArray())
                foreach (var w in dc[2].EnumerateArray()) places[w.GetString()!.ToLowerInvariant()] = (w.GetString()!, dc[0].GetString()!, dc[1].GetString()!);
            this.listings = listings;
        }
        public Dictionary<string, (string Name, string Dc, string Region)> Places() => places;
        public (Dictionary<int, List<MarketListing>> Listings, (string Problem, DateTime Since)? Stale) ListingsIn(string scope, List<int> ids)
        {
            var result = new Dictionary<int, List<MarketListing>>();
            if (!listings.TryGetProperty(scope, out var byItem)) return (result, null);
            foreach (var id in ids)
                if (byItem.TryGetProperty(id.ToString(System.Globalization.CultureInfo.InvariantCulture), out var list))
                    result[id] = list.EnumerateArray().Select(l => new MarketListing(l.GetProperty("pricePerUnit").GetDouble(), l.GetProperty("quantity").GetInt32(),
                        l.GetProperty("hq").GetBoolean(), l.GetProperty("retainerName").GetString(), l.TryGetProperty("worldName", out var w) ? w.GetString() : null)).ToList();
            return (result, null);
        }
    }

    // The list as the app's get-shopping-list answers it (the same names and the same fields in each section).
    private static System.Text.Json.Nodes.JsonNode ShopJson(ShoppingResult r)
    {
        if (r.Error != null) return new JsonObject { ["success"] = false, ["error"] = r.Error };
        if (r.Empty) return new JsonObject { ["success"] = true, ["empty"] = true };
        JsonNode? Market(ShopMarket? m) => m == null ? null : new JsonObject { ["price"] = m.Price, ["world"] = m.World,
            ["listings"] = new JsonArray(m.Listings.Select(l => (JsonNode)new JsonObject { ["price"] = l.Price, ["quantity"] = l.Quantity, ["world"] = l.World }).ToArray()) };
        JsonNode? Buying(ShopBuying? x) => x == null ? null : new JsonObject { ["gil"] = x.Gil, ["got"] = x.Got, ["wanted"] = x.Wanted };
        JsonNode? Breakdown(List<ShopBreakdown>? list) => list == null ? null : new JsonArray(list.Select(x => (JsonNode)new JsonObject { ["itemId"] = x.ItemId, ["name"] = x.Name,
            ["amount"] = x.Amount, ["each"] = x.Each, ["how"] = x.How, ["world"] = x.World, ["parts"] = Breakdown(x.Parts) }).ToArray());
        JsonObject Row(ShopRow x, string kind)
        {
            var o = new JsonObject { ["itemId"] = x.ItemId, ["need"] = x.Need };
            if (kind == "part") { o["crafts"] = x.Crafts; o["job"] = x.Job; if (x.NoRecipe) o["noRecipe"] = true; }
            else if (kind == "craft") { o["have"] = x.Have; o["make"] = x.Short; o["crafts"] = x.Crafts; o["yield"] = x.Yield; o["job"] = x.Job; }
            else { o["have"] = x.Have; o["short"] = x.Short; }
            o["name"] = x.Name;
            o["places"] = new JsonArray(x.Places.Select(p => (JsonNode)new JsonObject { ["who"] = p.Who, ["where"] = p.Where, ["qty"] = p.Qty }).ToArray());
            o["vendorPrice"] = x.VendorPrice;
            if (kind == "part") o["made"] = x.Made;
            if (kind == "buy") o["cost"] = x.Cost;
            if (r.Priced && kind != "buy")
            {
                o["market"] = Market(x.Market);
                if (kind == "part") { o["makeCost"] = x.MakeCost; o["makeBreakdown"] = Breakdown(x.MakeBreakdown); o["cheaper"] = x.Cheaper; }
                else o["cheapest"] = x.Cheapest;
                o["buying"] = Buying(x.Buying);
            }
            return o;
        }
        JsonArray Rows(List<ShopRow> rows, string kind) => new(rows.Select(x => (JsonNode)Row(x, kind)).ToArray());
        var m = r.Market;
        var market = new JsonObject { ["enabled"] = m.Enabled };
        if (m.Enabled)
        {
            market["choices"] = new JsonArray((m.Choices ?? new()).Select(c => (JsonNode)new JsonObject { ["value"] = c.Value, ["label"] = c.Label, ["kind"] = c.Kind, ["scope"] = c.Scope }).ToArray());
            if (m.Scope != null) { market["scope"] = m.Scope; market["label"] = m.Label; market["kind"] = m.Kind; }
            if (m.Error != null) market["error"] = m.Error;
            else market["stale"] = m.Stale is { } st ? new JsonObject { ["problem"] = st.Problem, ["since"] = (st.Since - DateTime.UnixEpoch).TotalMilliseconds } : null;
        }
        var o = new JsonObject
        {
            ["success"] = true, ["vendorsLoaded"] = r.VendorsLoaded, ["buyFromVendors"] = r.BuyFromVendors,
            ["parts"] = Rows(r.Parts, "part"), ["craft"] = Rows(r.Craft, "craft"), ["gather"] = Rows(r.Gather, "gather"), ["crystals"] = Rows(r.Crystals, "gather"),
            ["buy"] = Rows(r.Buy, "buy"), ["buyCost"] = r.BuyCost, ["market"] = market,
        };
        if (r.Totals is { } t)
            o["marketTotals"] = new JsonObject { ["parts"] = t.Parts, ["partsUnlisted"] = t.PartsUnlisted, ["partsTooFew"] = t.PartsTooFew,
                ["materials"] = t.Materials, ["materialsUnlisted"] = t.MaterialsUnlisted, ["materialsTooFew"] = t.MaterialsTooFew };
        return o;
    }

    // The first place two JSON answers differ (numbers as the same double, objects with the same fields), or null.
    // Import Fight Club Data: the app's own reading of some awkward route spreadsheets and the table after its import
    // (the app's tests/db/fight-club-fixture.js), against the plugin's reading and its import into PostgreSQL and SQLite.
    private static async Task FightClubAgainstTheApp(NpgsqlConnection conn)
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "fight-club-fixture.json")));
        var root = doc.RootElement;
        static JsonNode? Value(object? v) => v switch { null => null, double d => JsonValue.Create(d), string t => JsonValue.Create(t), var x => JsonValue.Create(x.ToString()) };
        static JsonObject AsApp(FightClubImport.ReadResult r)
        {
            var o = new JsonObject { ["success"] = r.Success };
            if (!r.Success)
            {
                o["stage"] = r.Stage;
                o["error"] = r.Error;
                o["actualHeaders"] = new JsonArray(r.ActualHeaders.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
                return o;
            }
            o["actualHeaders"] = new JsonArray(r.ActualHeaders.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
            o["missingOptional"] = new JsonArray(r.MissingOptional.Select(h => (JsonNode?)JsonValue.Create(h)).ToArray());
            o["rows"] = new JsonArray(r.Rows.Select(row =>
            {
                var data = new JsonObject();
                foreach (var (k, v) in row.Data) data[k] = Value(v);
                return (JsonNode?)new JsonObject
                {
                    ["rowNumber"] = row.RowNumber, ["data"] = data,
                    ["issues"] = new JsonArray(row.Issues.Select(i => (JsonNode?)JsonValue.Create(i)).ToArray()), ["hasIssue"] = row.HasIssue,
                };
            }).ToArray());
            return o;
        }

        Check(FightClubImport.Columns.Select(c => $"{c.Header}|{c.DbColumn}|{(c.Numeric ? "numeric" : "text")}|{c.Required}").SequenceEqual(
                root.GetProperty("columns").EnumerateArray().Select(c => $"{c.GetProperty("csvHeader").GetString()}|{c.GetProperty("dbColumn").GetString()}|{c.GetProperty("type").GetString()}|{c.GetProperty("required").GetBoolean()}")),
            "fight club: the same columns as the app's import");

        await Exec(conn, "DROP DATABASE IF EXISTS fleet_fight_club_plugin");
        await Exec(conn, "CREATE DATABASE fleet_fight_club_plugin");
        var given = Environment.GetEnvironmentVariable("FLEET_TEST_DB") ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";
        await using (var db = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(given) { Database = "fleet_fight_club_plugin", Pooling = false }.ConnectionString))
        await using (var lite = new System.Data.SQLite.SQLiteConnection("Data Source=:memory:"))
        {
            await db.OpenAsync();
            await lite.OpenAsync();
            await Exec(db, root.GetProperty("schema").GetString()!);
            await Exec(db, "INSERT INTO fight_club_routes (route) VALUES ('OLD1'), ('OLD2')");
            await using (var make = lite.CreateCommand())
            {
                make.CommandText = root.GetProperty("sqliteTables").GetString()! + "; INSERT INTO fight_club_routes (route) VALUES ('OLD1'), ('OLD2');";
                await make.ExecuteNonQueryAsync();
            }
            var tableSql = root.GetProperty("table").GetString()!;
            foreach (var book in root.GetProperty("books").EnumerateArray())
            {
                var about = book.GetProperty("about").GetString();
                var read = FightClubImport.Read(Convert.FromBase64String(book.GetProperty("xlsx").GetString()!));
                using (var mine = JsonDocument.Parse(AsApp(read).ToJsonString()))
                {
                    var diff = JsonDiff(mine.RootElement, book.GetProperty("read"), "read");
                    Check(diff == null, $"fight club: {about}: read as the app reads it" + (diff == null ? "" : $"\n   {diff}"));
                }
                if (book.GetProperty("imported").ValueKind != JsonValueKind.Object) continue;
                var imported = book.GetProperty("imported");
                var label = imported.GetProperty("patchLabel").GetString();
                var rows = read.Rows.Where(r => !r.HasIssue).ToList();
                Check(rows.Select(r => r.RowNumber).SequenceEqual(imported.GetProperty("rowNumbers").EnumerateArray().Select(n => n.GetInt32())),
                    $"fight club: {about}: the same rows ticked");
                var count = await FightClubImport.Commit(db, rows, label);
                var want = imported.GetProperty("table");
                var table = new JsonArray();
                await using (var cmd = new NpgsqlCommand(tableSql, db))
                await using (var r = await cmd.ExecuteReaderAsync())
                    while (await r.ReadAsync())
                    {
                        var row = new JsonObject();
                        for (var i = 0; i < r.FieldCount; i++)
                            row[r.GetName(i)] = r.IsDBNull(i) ? null : r.GetValue(i) is int n ? JsonValue.Create(n) : JsonValue.Create(Convert.ToString(r.GetValue(i), System.Globalization.CultureInfo.InvariantCulture));
                        table.Add(row);
                    }
                using (var mine = JsonDocument.Parse(table.ToJsonString()))
                {
                    var diff = JsonDiff(mine.RootElement, want, "table");
                    Check(count == imported.GetProperty("answer").GetProperty("imported").GetInt32() && diff == null,
                        $"fight club: {about}: imported into PostgreSQL as the app imports it ({count} routes)" + (diff == null ? "" : $"\n   {diff}"));
                }

                // The one-file version: the same rows in SQLite, numbers as numbers.
                await FightClubImport.Commit(lite, rows, label);
                var liteRows = new List<string>();
                var stamped = true;
                await using (var cmd = lite.CreateCommand())
                {
                    cmd.CommandText = "SELECT id, route, " + string.Join(", ", FightClubImport.Columns.Where(c => c.DbColumn != "route").Select(c => c.DbColumn)) + ", patch_label, imported_at FROM fight_club_routes ORDER BY id";
                    await using var r = await cmd.ExecuteReaderAsync();
                    while (await r.ReadAsync())
                    {
                        var cells = new List<string>();
                        for (var i = 0; i < r.FieldCount - 1; i++)
                            cells.Add(r.IsDBNull(i) ? "null" : r.GetValue(i) is double d ? d.ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                                : r.GetValue(i) is long l ? l.ToString(System.Globalization.CultureInfo.InvariantCulture) : r.GetValue(i).ToString()!);
                        stamped &= !r.IsDBNull(r.FieldCount - 1);
                        liteRows.Add(string.Join("|", cells));
                    }
                }
                var numeric = FightClubImport.Columns.Where(c => c.Numeric).Select(c => c.DbColumn).ToHashSet();
                var wantRows = want.EnumerateArray().Select(w => string.Join("|", w.EnumerateObject().Select(p =>
                    p.Value.ValueKind == JsonValueKind.Null ? "null"
                    : p.Value.ValueKind == JsonValueKind.Number ? p.Value.GetInt64().ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : numeric.Contains(p.Name) ? double.Parse(p.Value.GetString()!, System.Globalization.CultureInfo.InvariantCulture).ToString("R", System.Globalization.CultureInfo.InvariantCulture)
                    : p.Value.GetString()!))).ToList();
                var firstDiff = liteRows.Zip(wantRows).FirstOrDefault(x => x.First != x.Second);
                Check(stamped && liteRows.SequenceEqual(wantRows), $"fight club: {about}: imported into SQLite the same, with when ({liteRows.Count} routes)"
                    + (liteRows.SequenceEqual(wantRows) ? "" : $"\n   plugin {firstDiff.First ?? $"{liteRows.Count} rows"}\n   app    {firstDiff.Second ?? $"{wantRows.Count} rows"}"));
            }
            // A failed import leaves the routes as they were.
            var before = await new NpgsqlCommand("SELECT count(*) FROM fight_club_routes", db).ExecuteScalarAsync();
            var bad = new FightClubImport.Row(2, FightClubImport.Columns.ToDictionary(c => c.DbColumn, c => (object?)null), new List<string>());
            var failed = false;
            try { await FightClubImport.Commit(db, new[] { bad }, null); } catch (PostgresException) { failed = true; }
            Check(failed && Equals(await new NpgsqlCommand("SELECT count(*) FROM fight_club_routes", db).ExecuteScalarAsync(), before),
                "fight club: an import that fails part way leaves the routes there were");
        }
        await Exec(conn, "DROP DATABASE fleet_fight_club_plugin");
        Check(!FightClubImport.Read(System.Text.Encoding.UTF8.GetBytes("just some text, not a spreadsheet")).Success && !root.GetProperty("notSpreadsheet").GetProperty("success").GetBoolean(),
            "fight club: something that isn't a spreadsheet isn't read (nor by the app)");
        Check(FightClubImport.JsNumberText(0.1 + 0.2) == "0.30000000000000004" && FightClubImport.JsNumberText(1.5e-7) == "1.5e-7" && FightClubImport.JsNumberText(1e21) == "1e+21"
              && FightClubImport.JsNumberText(1e20) == "100000000000000000000" && FightClubImport.JsNumberText(-0.0015) == "-0.0015" && FightClubImport.JsNumberText(-0.0) == "0"
              && FightClubImport.JsNumberText(123456.789) == "123456.789" && FightClubImport.JsNumberText(0.000001) == "0.000001" && FightClubImport.JsNumberText(1e-7) == "1e-7"
              && FightClubImport.JsNumberText(2.5e25) == "2.5e+25" && FightClubImport.JsNumberText(5e-324) == "5e-324",
            "fight club: numbers written as JavaScript writes them");
        Check(FightClubImport.JsNumber(" 12 ") == 12 && FightClubImport.JsNumber("") == 0 && FightClubImport.JsNumber("0b101") == 5 && FightClubImport.JsNumber("0o17") == 15
              && double.IsNaN(FightClubImport.JsNumber("-0x1")) && double.IsNaN(FightClubImport.JsNumber("12abc")) && FightClubImport.JsNumber("5.") == 5
              && double.IsNaN(FightClubImport.JsNumber(".")) && FightClubImport.JsNumber("-Infinity") == double.NegativeInfinity && double.IsNaN(FightClubImport.JsNumber("infinity")),
            "fight club: text read as a number as JavaScript reads it");
    }

    // The looks (Settings → Look): the same colours as the app's shared/themes.js, and what they mean in each.
    private static void LooksAgainstTheApp()
    {
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "themes-fixture.json")));
        var app = doc.RootElement.GetProperty("themes").EnumerateArray().ToList();
        Check(app.Select(t => t.GetProperty("key").GetString()).SequenceEqual(Theme.Looks.Select(l => l.Key)), "looks: the same looks as the app's, in the same order");
        foreach (var t in app)
        {
            var key = t.GetProperty("key").GetString()!;
            var mine = Theme.Looks.FirstOrDefault(l => l.Key == key);
            if (mine == null) continue;
            var name = t.GetProperty("name").GetString();
            if (t.GetProperty("tokens").ValueKind != JsonValueKind.Object)
            {
                Check(mine.Tokens == null && mine.Name == name, $"looks: {name} is Dalamud's own style here too");
                continue;
            }
            var k = t.GetProperty("tokens");
            string S(string p) => k.TryGetProperty(p, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
            var m = mine.Tokens!;
            var want = new[] { S("bg"), S("sidebar"), S("panel"), S("modal"), S("hover"), S("hoverStrong"), S("border"), S("borderStrong"), S("text"),
                S("textBright"), S("textSoft"), S("dim"), S("faint"), S("accent"), S("accentStrong"), S("link"), k.GetProperty("rowAlt")[0].GetString()!,
                k.GetProperty("rowAlt")[1].GetDouble().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), S("check"),
                k.GetProperty("headerAlpha").GetDouble().ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), k.GetProperty("light").GetBoolean().ToString(),
                S("heading"), (k.TryGetProperty("glow", out var g) && g.GetBoolean()).ToString(), S("good"), S("warn"), S("danger"), S("gil"), S("address"), S("fc"), S("fcPoints"),
                string.Join(",", new[] { "tank", "tankBase", "healer", "healerBase", "dps", "dpsBase" }.Select(r => k.GetProperty("roles").GetProperty(r).GetString())),
                string.Join(",", new[] { "good", "warn", "danger" }.Select(r => k.GetProperty("cb").GetProperty(r).GetString())) };
            var have = new[] { m.Bg, m.Sidebar, m.Panel, m.Modal, m.Hover, m.HoverStrong, m.Border, m.BorderStrong, m.Text, m.TextBright, m.TextSoft, m.Dim,
                m.Faint, m.Accent, m.AccentStrong, m.Link, m.RowAlt, m.RowAltAlpha.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), m.Check,
                m.HeaderAlpha.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture), m.Light.ToString(), m.Heading ?? "", m.Glow.ToString(),
                m.Good, m.Warn, m.Danger, m.Gil, m.Address, m.Fc, m.FcPoints, string.Join(",", m.Roles), string.Join(",", m.Cb) };
            var diff = want.Zip(have).Select((x, i) => (x, i)).FirstOrDefault(z => z.x.First != z.x.Second);
            Check(want.SequenceEqual(have) && mine.Name == name && mine.About == t.GetProperty("about").GetString(),
                $"looks: {name} has the app's colours" + (want.SequenceEqual(have) ? "" : $" (colour {diff.i}: app {diff.x.First}, plugin {diff.x.Second})"));
            var normal = Theme.Status(mine.Choice, false);
            var cb = Theme.Status(mine.Choice, true);
            Check(normal.Good == Theme.Hex(m.Good) && normal.Gil == Theme.Hex(m.Gil) && normal.Roles != null && cb.Good == Theme.Hex(m.Cb[0])
                  && cb.Warn == Theme.Hex(m.Cb[1]) && cb.Danger == Theme.Hex(m.Cb[2]) && cb.Gil == null && cb.Roles == null && cb.Address == null,
                $"looks: {name}: colour-blind friendly swaps fine, a look and urgent for its own, and leaves gil, addresses and classes plain");
        }
        Check(Theme.Info((ThemeChoice)99).Choice == ThemeChoice.Fleet, "looks: an unknown saved look is the default");
        Check((int)ThemeChoice.Fleet == 0 && (int)ThemeChoice.Dalamud == 1 && (int)ThemeChoice.HighContrast == 2, "looks: the looks saved before keep their numbers");
    }

    private static string? JsonDiff(System.Text.Json.JsonElement a, System.Text.Json.JsonElement b, string at)
    {
        if (a.ValueKind != b.ValueKind && !(a.ValueKind is JsonValueKind.True or JsonValueKind.False && b.ValueKind is JsonValueKind.True or JsonValueKind.False))
            return $"{at}: plugin {a.GetRawText()}, app {b.GetRawText()}";
        switch (a.ValueKind)
        {
            case JsonValueKind.Object:
                var ak = a.EnumerateObject().Select(p => p.Name).ToHashSet();
                var bk = b.EnumerateObject().Select(p => p.Name).ToHashSet();
                if (!ak.SetEquals(bk)) return $"{at}: plugin has [{string.Join(",", ak.Except(bk))}], app has [{string.Join(",", bk.Except(ak))}]";
                foreach (var k in ak) if (JsonDiff(a.GetProperty(k), b.GetProperty(k), $"{at}.{k}") is { } d) return d;
                return null;
            case JsonValueKind.Array:
                if (a.GetArrayLength() != b.GetArrayLength()) return $"{at}: plugin {a.GetArrayLength()} long, app {b.GetArrayLength()}";
                for (var k = 0; k < a.GetArrayLength(); k++) if (JsonDiff(a[k], b[k], $"{at}[{k}]") is { } d) return d;
                return null;
            case JsonValueKind.Number:
                return a.GetDouble() == b.GetDouble() ? null : $"{at}: plugin {a.GetRawText()}, app {b.GetRawText()}";
            case JsonValueKind.String:
                return a.GetString() == b.GetString() ? null : $"{at}: plugin {a.GetRawText()}, app {b.GetRawText()}";
            default:
                return a.GetRawText() == b.GetRawText() ? null : $"{at}: plugin {a.GetRawText()}, app {b.GetRawText()}";
        }
    }

    private static void RoutesAgainstTheApp()
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "routes-fixture.json");
        using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var game = GameFrom(doc.RootElement.GetProperty("game"));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
        {
            var input = RoutesInputsFrom(c.GetProperty("inputs"));
            var plan = SubRoutes.Plan(input, game, c.GetProperty("now").GetDouble());
            var mine = plan.OrderBy(s => s.Cid).ThenBy(s => s.SubName, StringComparer.Ordinal).Select(RouteLine).ToList();
            var app = c.GetProperty("expected").EnumerateArray()
                .OrderBy(e => long.Parse(e.GetProperty("cid").GetString()!, System.Globalization.CultureInfo.InvariantCulture))
                .ThenBy(e => e.GetProperty("sub_name").GetString(), StringComparer.Ordinal).Select(AppRouteLine).ToList();
            var first = Enumerable.Range(0, Math.Max(mine.Count, app.Count)).FirstOrDefault(k => k >= mine.Count || k >= app.Count || mine[k] != app[k], -1);
            // Look ahead on the Parts page (14 days): what's needed now, then the parts of the next builds due by then.
            var i = c.GetProperty("inputs");
            static string? PS(System.Text.Json.JsonElement e, string k) => e.GetProperty(k).ValueKind == System.Text.Json.JsonValueKind.Null ? null
                : e.GetProperty(k).ValueKind == System.Text.Json.JsonValueKind.String ? e.GetProperty(k).GetString() : e.GetProperty(k).GetRawText();
            static int? PI(System.Text.Json.JsonElement e, string k) => PS(e, k) is { } t ? (int)double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
            static long? PL(System.Text.Json.JsonElement e, string k) => PS(e, k) is { } t ? long.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
            static double? PD(System.Text.Json.JsonElement e, string k) => PS(e, k) is { } t ? double.Parse(t, System.Globalization.CultureInfo.InvariantCulture) : null;
            static bool PB(System.Text.Json.JsonElement e, string k) => e.GetProperty(k).ValueKind == System.Text.Json.JsonValueKind.True;
            var partsInput = new PartsInputs(input.Characters,
                i.GetProperty("partsSubs").EnumerateArray().Select(e => new PartsSub(PL(e, "cid")!.Value, PS(e, "sub_name") ?? "", PI(e, "slot"), PI(e, "rank") ?? 0,
                    PI(e, "part1"), PI(e, "part2"), PI(e, "part3"), PI(e, "part4"), PB(e, "craft_enabled"))).ToList(),
                input.Rules,
                i.GetProperty("partsFightClub").EnumerateArray().Select(e => new PartsFightClubRoute(PL(e, "id")!.Value, PS(e, "route") ?? "", PD(e, "hours"), PS(e, "repair_times"),
                    PD(e, "gil_sub_day"), PS(e, "rec_build_notes"), PD(e, "repair_kit_cost") ?? 0)).ToList(),
                i.GetProperty("spares").EnumerateArray().Select(e => new PartsSpare(PL(e, "cid"), PS(e, "fc_id"), PB(e, "is_retainer"), PL(e, "retainer_id"), PS(e, "retainer_name"),
                    PI(e, "sorted_container") ?? 0, PI(e, "sorted_slot_index") ?? 0, PI(e, "item_id") ?? 0, PI(e, "quantity") ?? 0)).ToList());
            var now = c.GetProperty("now").GetDouble();
            var ahead = PartsPlanner.Plan(partsInput, SubRoutes.Upcoming(plan, now, 14));
            var mineParts = ahead.Select(n => string.Join(" | ", n.Cid, n.SubName, n.Rank, n.Slot, n.NeededClass, n.NeededModified, n.SpareLocation, n.RecycledFrom, N(n.DueAt))).ToList();
            var appParts = c.GetProperty("expectedParts").EnumerateArray().Select(e => string.Join(" | ", PS(e, "cid"), PS(e, "sub_name"), PI(e, "rank"), PS(e, "slot"),
                PS(e, "needed_class"), PB(e, "needed_modified"), PS(e, "spare_location"), PS(e, "recycled_from"), N(PD(e, "due_at")))).ToList();
            var badPart = Enumerable.Range(0, Math.Max(mineParts.Count, appParts.Count)).FirstOrDefault(k => k >= mineParts.Count || k >= appParts.Count || mineParts[k] != appParts[k], -1);
            Check(badPart < 0 && PartsPlanner.Plan(partsInput).Count == ahead.Count(n => !n.Ahead),
                $"routes: Look ahead on the Parts page (14 days) the same as the app's, {c.GetProperty("name").GetString()} ({appParts.Count} parts, {ahead.Count(n => n.Ahead)} for builds coming up)"
                + (badPart < 0 ? "" : $"\n   part {badPart + 1}:\n   plugin [{(badPart < mineParts.Count ? mineParts[badPart] : "none")}]\n   app    [{(badPart < appParts.Count ? appParts[badPart] : "none")}]"));
            // The Shopping List, asked each way the fixture asked the app (Look ahead, Buy from vendors, Live Market Prices
            // and Prices from), from the same ticks, held items, made-up recipes and made-up Universalis.
            var shopGame = ShopGameFrom(doc.RootElement.GetProperty("game").GetProperty("shop"));
            var held = i.GetProperty("shoppingHeld").EnumerateArray().Select(e => new HeldRow(PI(e, "item_id") ?? 0, PS(e, "who") ?? "", PS(e, "place") ?? "", PL(e, "qty") ?? 0)).ToList();
            var ticks = i.GetProperty("shoppingProgress").EnumerateArray().Select(e => PS(e, "progress")).FirstOrDefault();
            var worlds = input.Characters.Where(ch => !string.IsNullOrEmpty(ch.World)).GroupBy(ch => ch.World!).Select(g => (g.Key, g.Count())).ToList();
            var exclude = i.GetProperty("retainers").EnumerateArray().Where(e => !string.IsNullOrEmpty(PS(e, "name"))).Select(e => Universalis.RetainerKey(PS(e, "name")!, PS(e, "world"))).ToHashSet();
            var fakeMarket = new FixtureMarket(doc.RootElement.GetProperty("places"), c.GetProperty("listings"));
            foreach (var run in c.GetProperty("shopping").EnumerateArray())
            {
                var options = run.GetProperty("options");
                var days = options.TryGetProperty("aheadDays", out var d) ? d.GetInt32() : 0;
                var longest = ShoppingMath.LongestAhead(days, ticks);
                var shopNeeds = longest == 0 ? PartsPlanner.Plan(partsInput) : PartsPlanner.Plan(partsInput, SubRoutes.Upcoming(plan, now, longest));
                // Held rows the other way round: the order they're read in mustn't matter.
                var list = ShoppingMath.Make(shopNeeds, now, days, ticks, shopGame, Enumerable.Reverse(held).ToList(), options.TryGetProperty("buyFromVendors", out var b) && b.GetBoolean(),
                    run.GetProperty("live").GetBoolean() ? fakeMarket : null, worlds, exclude, options.TryGetProperty("priceScope", out var ps) ? ps.GetString() : null);
                using var myList = System.Text.Json.JsonDocument.Parse(ShopJson(list).ToJsonString());
                var diff = JsonDiff(myList.RootElement, run.GetProperty("result"), "");
                Check(diff == null, $"shopping: the Shopping List the same as the app's, {c.GetProperty("name").GetString()} {options.GetRawText()}"
                    + (list.Parts.Count > 0 ? $" ({list.Parts.Count} parts, {list.Craft.Count} to craft first, {list.Gather.Count + list.Crystals.Count} to gather{(list.Priced ? ", priced" : "")})" : "")
                    + (diff == null ? "" : $"\n   {diff}"));
            }
            // Loot per Voyage on the Income page, at vendor prices and at (made-up) market prices.
            var expectedLoot = c.GetProperty("expectedLoot");
            var lootRows = i.GetProperty("loot").EnumerateArray().Select(e => new VoyageLootRow(PS(e, "fc_id") ?? "", PL(e, "sub_register") ?? 0, PD(e, "returned_at") ?? 0,
                PI(e, "sector") ?? 0, PS(e, "sub_name"), PI(e, "primary_item"), PI(e, "primary_count"), PI(e, "additional_item"), PI(e, "additional_count"), PI(e, "leg"),
                PI(e, "window_days") ?? 7)).ToList();
            var itemPrices = doc.RootElement.GetProperty("itemPrices").EnumerateObject().ToDictionary(p => int.Parse(p.Name, System.Globalization.CultureInfo.InvariantCulture), p => p.Value.GetDouble());
            double LootPrice(int id) => id >= 22500 && id <= 22507 ? new[] { 8000, 9000, 10000, 13000, 27000, 28500, 30000, 34500 }[id - 22500] : itemPrices.GetValueOrDefault(id);
            var letters = game.Sectors.Where(s => s.Location.Length > 0).ToDictionary(s => s.Id, s => s.Location);
            var windowDays = expectedLoot.GetProperty("windowDays").GetInt32();
            var loot = LootMath.Summary(lootRows, windowDays, LootPrice, letters, input.Characters, plan);
            static string G(double v) => v.ToString("G9", System.Globalization.CultureInfo.InvariantCulture);
            var mineLoot = new List<string> { $"totals {loot.VoyageCount} {G(loot.Gil)} {G(loot.PerDay)}" }
                .Concat(loot.Subs.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"sub {s.Key} {s.SubName} {s.Voyages} {G(s.Gil)} {G(s.PerVoyage)} {G(s.PerDay)} {s.Route} {G(s.LastAt)} {s.Character} {s.World} {N(s.ExpectedPerDay)}"))
                .Concat(loot.Routes.OrderBy(r => r.Route, StringComparer.Ordinal).Select(r => $"route {r.Route} {r.Voyages} {r.Subs} {G(r.Gil)} {G(r.PerVoyage)}")).ToList();
            var t = expectedLoot.GetProperty("totals");
            var appLoot = new List<string> { $"totals {t.GetProperty("voyages").GetInt32()} {G(t.GetProperty("gil").GetDouble())} {G(t.GetProperty("perDay").GetDouble())}" }
                .Concat(expectedLoot.GetProperty("subs").EnumerateArray().OrderBy(e => $"{PS(e, "fcId")}|{PS(e, "register")}", StringComparer.Ordinal)
                    .Select(e => $"sub {PS(e, "fcId")}|{PS(e, "register")} {PS(e, "subName")} {PI(e, "voyages")} {G(PD(e, "gil")!.Value)} {G(PD(e, "perVoyage")!.Value)} {G(PD(e, "perDay")!.Value)} "
                        + $"{PS(e, "route")} {G(PD(e, "lastAt")!.Value)} {PS(e, "character")} {PS(e, "world")} {N(PD(e, "expectedPerDay"))}"))
                .Concat(expectedLoot.GetProperty("routes").EnumerateArray().OrderBy(e => PS(e, "route"), StringComparer.Ordinal)
                    .Select(e => $"route {PS(e, "route")} {PI(e, "voyages")} {PI(e, "subs")} {G(PD(e, "gil")!.Value)} {G(PD(e, "perVoyage")!.Value)}")).ToList();
            // Market prices: made up the same way as the fixture (by world, scope and item).
            static double? FakePrice(string world, string scope, int id)
            {
                var h = 7;
                foreach (var ch in $"{world}|{scope}|{id}") h = (h * 31 + ch) % 100003;
                return h % 5 == 0 ? null : 100 + h % 9000;
            }
            var lootItems = lootRows.SelectMany(r => new[] { r.PrimaryItem, r.AdditionalItem }).OfType<int>().Where(x => x != 0).Distinct().ToList();
            var worldOf = loot.Subs.Where(s => s.World != null).ToDictionary(s => s.Key, s => s.World!);
            foreach (var scope in Universalis.Scopes)
            {
                var m = LootMath.Market(loot.Voyages, worldOf, w => lootItems.Select(id => (id, p: FakePrice(w, scope, id))).Where(x => x.p != null).ToDictionary(x => x.id, x => x.p!.Value),
                    letters, windowDays);
                mineLoot.Add($"market {scope} {m.Voyages} {G(m.Gil)} {G(m.PerDay)}");
                mineLoot.AddRange(m.Subs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"market {scope} sub {kv.Key} {kv.Value.Voyages} {G(kv.Value.Gil)} {G(kv.Value.PerVoyage)} {G(kv.Value.PerDay)}"));
                mineLoot.AddRange(m.Routes.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => $"market {scope} route {kv.Key} {kv.Value.Voyages} {G(kv.Value.Gil)} {G(kv.Value.PerVoyage)}"));
                var am = expectedLoot.GetProperty("market").GetProperty(scope);
                var at = am.GetProperty("totals");
                appLoot.Add($"market {scope} {at.GetProperty("voyages").GetInt32()} {G(at.GetProperty("gil").GetDouble())} {G(at.GetProperty("perDay").GetDouble())}");
                appLoot.AddRange(am.GetProperty("subs").EnumerateArray().OrderBy(e => PS(e, "key"), StringComparer.Ordinal)
                    .Select(e => $"market {scope} sub {PS(e, "key")} {PI(e, "voyages")} {G(PD(e, "gil")!.Value)} {G(PD(e, "perVoyage")!.Value)} {G(PD(e, "perDay")!.Value)}"));
                appLoot.AddRange(am.GetProperty("routes").EnumerateArray().OrderBy(e => PS(e, "route"), StringComparer.Ordinal)
                    .Select(e => $"market {scope} route {PS(e, "route")} {PI(e, "voyages")} {G(PD(e, "gil")!.Value)} {G(PD(e, "perVoyage")!.Value)}"));
            }
            // The window's background working out (MarketCache) gives the same as working it out directly, from live
            // listings made up the same way; none without live prices.
            var liveMarkets = worldOf.Values.Distinct().ToDictionary(w => w, w => new WorldMarket(new MarketPlaceNames(w, "Dynamis", "NA"),
                Universalis.Scopes.ToDictionary(sc => sc, sc => lootItems.Where(id => FakePrice(w, sc, id) != null)
                    .ToDictionary(id => id, id => new List<MarketListing> { new(FakePrice(w, sc, id)!.Value, 1, false, "Seller", null) })), null));
            var liveRead = new MarketRead(new MarketSettings(7, true), new List<MarketAccountRow>(), new List<ListingRow>(), new LiveMarket(liveMarkets, new HashSet<string>(), null));
            var cacheNow = new MarketCache(work => { work(); return Task.CompletedTask; });
            cacheNow.LootMarketFor(liveRead, loot, letters, LootPrice);
            var cached = cacheNow.LootMarketFor(liveRead, loot, letters, LootPrice);
            var vendorAll = loot.Voyages.SelectMany(v => v.Items.Keys).Distinct().ToDictionary(id => id, LootPrice);
            var sameAsDirect = cached != null && Universalis.Scopes.All(sc =>
            {
                var direct = LootMath.Market(loot.Voyages, worldOf, w => Universalis.ScopePrices(liveMarkets[w], sc, vendorAll, new HashSet<string>()), letters, windowDays);
                return Math.Abs(direct.Gil - cached[sc].Gil) < 1e-6 && direct.Voyages == cached[sc].Voyages && direct.Subs.Count == cached[sc].Subs.Count;
            });
            Check(sameAsDirect && new MarketCache().LootMarketFor(liveRead with { Live = null }, loot, letters, LootPrice) == null,
                $"routes: Loot per Voyage at market prices worked out in the background the same as directly, {c.GetProperty("name").GetString()}");
            var badLoot = Enumerable.Range(0, Math.Max(mineLoot.Count, appLoot.Count)).FirstOrDefault(k => k >= mineLoot.Count || k >= appLoot.Count || mineLoot[k] != appLoot[k], -1);
            Check(badLoot < 0 && loot.Subs.Count > 0, $"routes: Loot per Voyage the same as the app's, at vendor and market prices, {c.GetProperty("name").GetString()} "
                + $"({loot.VoyageCount} voyages over {windowDays} days, {loot.Subs.Count} subs, {loot.Routes.Count} routes, {loot.Subs.Count(s => s.ExpectedPerDay != null)} with expected)"
                + (badLoot < 0 ? "" : $"\n   line {badLoot + 1}:\n   plugin [{(badLoot < mineLoot.Count ? mineLoot[badLoot] : "none")}]\n   app    [{(badLoot < appLoot.Count ? appLoot[badLoot] : "none")}]"));
            // How close the Next Rank dates came (the Subs page's forecast line).
            var saved = i.GetProperty("forecasts").EnumerateArray().Select(e => new SavedForecast(PL(e, "cid") ?? 0, PI(e, "slot") ?? 0, PI(e, "rank") ?? 0,
                PD(e, "made_at") ?? 0, PD(e, "predicted_at") ?? 0, PS(e, "sub_name"), PS(e, "fc_id"), PS(e, "character_name"))).ToList();
            var rankHistory = i.GetProperty("rankHistory").EnumerateArray().Select(e => new SubHistoryPoint(PL(e, "cid") ?? 0, PS(e, "metric") ?? "", PD(e, "recorded_at") ?? 0, PD(e, "value") ?? 0)).ToList();
            var returns = i.GetProperty("returns").EnumerateArray().Select(e => new VoyageReturn(PS(e, "fc_id") ?? "", PS(e, "sub_name") ?? "", PD(e, "returned_at") ?? 0)).ToList();
            var accuracy = ForecastMath.Check(saved, rankHistory, returns);
            var appAcc = c.GetProperty("expectedAccuracy");
            static string CheckLine(string? sub, string character, int rank, double at, double predicted, double made, double off) =>
                $"{sub} {character} {rank} {Math.Round(at)} {Math.Round(predicted)} {Math.Round(made)} {Math.Round(off)}";
            var mineAcc = new[] { $"{accuracy.Count} {N(accuracy.AverageOff is double ao ? Math.Round(ao) : null)}" }
                .Concat(accuracy.Checks.Select(x => CheckLine(x.SubName, x.Character, x.Rank, x.At, x.PredictedAt, x.MadeAt, x.Off)).OrderBy(x => x, StringComparer.Ordinal)).ToList();
            var appAccLines = new[] { $"{appAcc.GetProperty("count").GetInt32()} {N(PD(appAcc, "averageOff") is double aao ? Math.Round(aao) : null)}" }
                .Concat(appAcc.GetProperty("checks").EnumerateArray().Select(e => CheckLine(PS(e, "subName"), PS(e, "characterName") ?? "", PI(e, "rank") ?? 0, PD(e, "at") ?? 0,
                    PD(e, "predictedAt") ?? 0, PD(e, "madeAt") ?? 0, PD(e, "off") ?? 0)).OrderBy(x => x, StringComparer.Ordinal)).ToList();
            var badAcc = Enumerable.Range(0, Math.Max(mineAcc.Count, appAccLines.Count)).FirstOrDefault(k => k >= mineAcc.Count || k >= appAccLines.Count || mineAcc[k] != appAccLines[k], -1);
            Check(badAcc < 0 && accuracy.Count > 0, $"routes: how close the Next Rank dates came, the same as the app's, {c.GetProperty("name").GetString()} ({accuracy.Count} rank-ups, "
                + $"{(accuracy.AverageOff ?? 0) / 3600000:0.0}h off on average)"
                + (badAcc < 0 ? "" : $"\n   line {badAcc + 1}:\n   plugin [{(badAcc < mineAcc.Count ? mineAcc[badAcc] : "none")}]\n   app    [{(badAcc < appAccLines.Count ? appAccLines[badAcc] : "none")}]"));
            Check(first < 0, $"routes: the same as the app's Subs tab, {c.GetProperty("name").GetString()} ({app.Count} subs:{plan.Count(s => s.TargetRoute.Length > 0)} with a Target Route, "
                + $"{plan.Count(s => s.CurrentGilDay != null)} a Current Gil/Day, {plan.Count(s => s.RankUp.NextRankAt != null)} a Next Rank date, {plan.Count(s => s.RankUp.NextBuild != null)} a Next Build)"
                + (first < 0 ? "" : $"\n   sub {first + 1}:\n   plugin [{(first < mine.Count ? mine[first] : "none")}]\n   app    [{(first < app.Count ? app[first] : "none")}]"));
        }
    }

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
                Check(ready.Count == 8 && ready.Count(x => !x.Sub.IsNew) == 3 && ready[0].Sub.Character == "Bex"
                      && PartsPlanner.ReadyText(ready) == "3 subs are ready for new parts, 5 new subs to build" && ready.Where(x => x.Sub.IsNew).All(x => x.Parts.Count == 4)
                      && craft.Sum(x => x.Count) == plan.Count(n => n.ToCraft)
                      && craft.Select(x => x.Slot).Distinct().SequenceEqual(new[] { "Hull", "Stern", "Bow", "Bridge" }),
                    "batch 4: subs ready for new parts and new subs to build (in Roster order, counted apart) and the parts still to craft by slot: "
                    + string.Join(", ", ready.Select(x => $"{x.Sub.Character} {x.Sub.SubName}")) + " / " + string.Join(", ", craft.Select(x => $"{x.Slot} {x.Needed} {x.Count}")));
            }
        Check(first < 0, $"batch 4: parts the same as the app's, {c.GetProperty("name").GetString()} ({app.Count} parts)"
                + (first < 0 ? "" : $": part {first + 1}: plugin [{(first < mine.Count ? mine[first] : "none")}], app [{(first < app.Count ? app[first] : "none")}]"));
        }
    }

    // 2.1: the plugin updates the database itself (DatabaseUpkeep), with the app's own files. Each way the app updated a
    // made-up database (the app's tests/db/plugin-database-fixture.js), done again here with the plugin's own code, must
    // leave exactly the same tables, columns, indexes, views and version records.
    private static async Task DatabaseAgainstTheApp(NpgsqlConnection conn)
    {
        var file = System.IO.Path.Combine(AppContext.BaseDirectory, "database-fixture.json");
        using var doc = JsonDocument.Parse(System.IO.File.ReadAllText(file));
        var root = doc.RootElement;
        var files = DatabaseUpkeep.Files.Embedded();
        Check(files.AppVersion == root.GetProperty("appVersion").GetString() && files.Latest == 16 && files.Updates.Count == 7 && files.Views != null,
            $"database: the plugin carries the app's files (app {files.AppVersion}, updates to {files.Latest})");
        var signature = root.GetProperty("signature").EnumerateObject().Select(p => (p.Name, Sql: p.Value.GetString()!)).ToList();
        var given = Environment.GetEnvironmentVariable("FLEET_TEST_DB") ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres";
        string Cs(string db) => new NpgsqlConnectionStringBuilder(given) { Database = db, Pooling = false }.ConnectionString;
        async Task<NpgsqlConnection> Fresh(string db)
        {
            await Exec(conn, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
            await Exec(conn, $"CREATE DATABASE {db}");
            var c = new NpgsqlConnection(Cs(db));
            await c.OpenAsync();
            return c;
        }
        async Task<string> Signature(NpgsqlConnection c)
        {
            var all = new JsonObject();
            foreach (var (name, sql) in signature)
            {
                try
                {
                    all[name] = new JsonArray((await Rows(c, sql)).Select(r => (JsonNode?)JsonValue.Create(Convert.ToString(r[0], System.Globalization.CultureInfo.InvariantCulture))).ToArray());
                }
                catch (PostgresException)
                {
                    all[name] = null;
                }
            }
            return all.ToJsonString();
        }
        static string Differs(string mine, JsonElement app)
        {
            var a = JsonNode.Parse(mine)!.AsObject();
            foreach (var p in app.EnumerateObject())
            {
                var m = a[p.Name]?.ToJsonString() ?? "null";
                var x = p.Value.ValueKind == JsonValueKind.Null ? "null" : JsonNode.Parse(p.Value.GetRawText())!.ToJsonString();
                if (m == x) continue;
                var mineList = a[p.Name]?.AsArray().Select(v => v!.ToString()).ToHashSet() ?? new HashSet<string>();
                var appList = p.Value.ValueKind == JsonValueKind.Array ? p.Value.EnumerateArray().Select(v => v.GetString()!).ToHashSet() : new HashSet<string>();
                return $"{p.Name}: only the plugin's: [{string.Join("; ", mineList.Except(appList).Take(3))}], only the app's: [{string.Join("; ", appList.Except(mineList).Take(3))}]";
            }
            return "";
        }

        foreach (var sc in root.GetProperty("scenarios").EnumerateObject())
        {
            var db = $"pdb_plugin_{sc.Name}";
            await using (var c = await Fresh(db))
            {
                var setup = sc.Value.GetProperty("setup").GetString()!;
                if (setup.Length > 0) await Exec(c, setup);
                var n = 0;
                foreach (var step in sc.Value.GetProperty("steps").EnumerateArray())
                {
                    n++;
                    var upTo = step.TryGetProperty("upTo", out var u) ? u.GetInt32() : 0;
                    var result = await DatabaseUpkeep.EnsureAsync(c, upTo > 0 ? files.UpTo(upTo) : files, "plugin test", wait: true);
                    var again = step.TryGetProperty("again", out _);
                    var diff = Differs(await Signature(c), step.GetProperty("expect"));
                    Check(diff == "" && (!again || result.Applied.Count == 0),
                        $"database: {sc.Name}, step {n}{(upTo > 0 ? $" (updates to {upTo}, no views)" : again ? " (again: nothing to do)" : "")}: the same as the app's"
                        + (diff == "" ? $" ({string.Join(", ", result.Applied.Take(4))}{(result.Applied.Count > 4 ? "..." : "")})" : $": {diff}"));
                }
            }
            await Exec(conn, $"DROP DATABASE IF EXISTS {db} WITH (FORCE)");
        }

        // The views' checksum is worked out as the app does, so the app and the plugin agree a database is up to date.
        var appChecksum = root.GetProperty("scenarios").GetProperty("empty").GetProperty("steps")[0].GetProperty("expect").GetProperty("viewsChecksum")[0].GetString();
        Check(DatabaseUpkeep.Checksum(files.Views!) == appChecksum && DatabaseUpkeep.Checksum("a\r\nb") == DatabaseUpkeep.Checksum("a\nb"),
            "database: the views' checksum is the app's (Windows line endings or not)");
        Check(DatabaseUpkeep.CompareVersions("1.9.0", "1.8.1") == 1 && DatabaseUpkeep.CompareVersions("1.9.0-test.31", "1.9.0") == 0
              && DatabaseUpkeep.CompareVersions("1.10.0", "1.9.9") == 1 && DatabaseUpkeep.CompareVersions("2", "2.0.0") == 0 && DatabaseUpkeep.CompareVersions("1.0.0", "1.0.1") == -1,
            "database: versions compared as the app does");

        await using (var c = await Fresh("pdb_plugin_rules"))
        {
            // Another copy (the app, or another game) is updating: this one leaves it to that one, without waiting.
            await using (var other = new NpgsqlConnection(Cs("pdb_plugin_rules")))
            {
                await other.OpenAsync();
                await Exec(other, $"SELECT pg_advisory_lock({DatabaseUpkeep.LockKey})");
                var busy = await DatabaseUpkeep.EnsureAsync(c, files, "plugin test");
                Check(busy.Busy && busy.Applied.Count == 0 && (await Rows(c, "SELECT to_regclass('companion_schema_version')"))[0][0] is null or DBNull,
                    "database: while another copy updates, the plugin leaves it alone and doesn't wait");
            }
            var made = await DatabaseUpkeep.EnsureAsync(c, files, "plugin test");
            Check(made.Created && made.Version == 16 && !made.Busy && made.Applied.Contains("views updated"), "database: then an empty database gets everything: " + made.Text);
            var recorded = await Rows(c, "SELECT app_version FROM companion_views");
            Check((string)recorded[0][0]! == files.AppVersion, "database: the views are recorded with the app version the plugin's files came from");

            // Newer views (a newer app) are never replaced with the plugin's; older ones are.
            await Exec(c, "UPDATE companion_views SET checksum = 'other', app_version = '99.0.0'");
            var kept = await DatabaseUpkeep.EnsureAsync(c, files, "plugin test");
            Check(kept.Applied.Count == 0 && (string)(await Rows(c, "SELECT checksum FROM companion_views"))[0][0]! == "other", "database: a newer app's views are left alone");
            await Exec(c, "UPDATE companion_views SET checksum = 'other', app_version = '1.0.0'");
            var replaced = await DatabaseUpkeep.EnsureAsync(c, files, "plugin test");
            Check(replaced.Applied.SequenceEqual(new[] { "views updated" }), "database: an older app's views are replaced");

            // A login that may only read: fine while nothing needs changing, a plain message when something does.
            await Exec(conn, "DROP ROLE IF EXISTS pdb_reader");
            await Exec(conn, "CREATE ROLE pdb_reader LOGIN PASSWORD 'reader'");
            await Exec(c, "GRANT SELECT ON ALL TABLES IN SCHEMA public TO pdb_reader");
            var readerCs = new NpgsqlConnectionStringBuilder(Cs("pdb_plugin_rules")) { Username = "pdb_reader", Password = "reader" }.ConnectionString;
            await using (var reader = new NpgsqlConnection(readerCs))
            {
                await reader.OpenAsync();
                var fine = await DatabaseUpkeep.EnsureAsync(reader, files, "plugin test");
                Check(!fine.Busy && fine.Applied.Count == 0 && fine.Version == 16, "database: a login that may only read is fine while nothing needs changing");
                // As if made by a copy that had only up to update 15: update 16 is still to do.
                await Exec(c, "UPDATE companion_schema_version SET version = 15");
                string? message = null;
                try
                {
                    await DatabaseUpkeep.EnsureAsync(reader, files, "plugin test");
                }
                catch (InvalidOperationException e)
                {
                    message = e.Message;
                }
                Check(message != null && message.Contains("\"pdb_reader\" is not allowed to change the tables"), "database: and says plainly when it can't apply an update: " + message);
            }
            var back = await DatabaseUpkeep.EnsureAsync(c, files, "plugin test");
            Check(back.Applied.SequenceEqual(new[] { "update 16: pc_last_read" }) && back.Version == 16, "database: the owner's login applies it (again: nothing changes twice): " + back.Text);
            var by = await Rows(c, "SELECT app_version FROM companion_schema_version WHERE version = 16");
            Check((string)by[0][0]! == "plugin test", "database: each version records who applied it");

            // A newer app or plugin updated it further: left alone, and said.
            await Exec(c, "INSERT INTO companion_schema_version (version, name) VALUES (99, 'from the future'); UPDATE companion_views SET checksum = 'future', app_version = '1.0.0'");
            var newer = await DatabaseUpkeep.EnsureAsync(c, files, "plugin test");
            Check(newer.Newer && newer.Applied.Count == 0 && (string)(await Rows(c, "SELECT checksum FROM companion_views"))[0][0]! == "future" && newer.Text.Contains("newer"),
                "database: one a newer copy updated further is left alone, views and all: " + newer.Text);
        }
        await Exec(conn, "DROP DATABASE IF EXISTS pdb_plugin_rules WITH (FORCE)");
        await Exec(conn, "DROP ROLE IF EXISTS pdb_reader");

        // When the plugin looks: first time, then not again for 10 minutes; a problem is kept and said once.
        await using (var c = await Fresh("pdb_plugin_keeper"))
        {
            var said = new List<string>();
            var keeper = new DatabaseKeeper(() => files, "plugin 2.1.0", said.Add);
            Check(keeper.StatusText == "Not looked at yet.", "database: nothing said before the first look");
            await keeper.RunIfDueAsync(c);
            Check(keeper.LastResult is { Created: true } && keeper.Problem == null && said.Count == 1 && said[0].StartsWith("Brought the database up to date (version 16)"),
                "database: the first read or sync brings it up to date and says so: " + keeper.StatusText);
            var recordedBy = await Rows(c, "SELECT app_version FROM companion_schema_version");
            Check((string)recordedBy[0][0]! == $"{files.AppVersion} (plugin 2.1.0)", "database: recorded as the plugin, with the app version of its files");
            await Exec(c, "DELETE FROM companion_schema_version WHERE version = 16");
            await keeper.RunIfDueAsync(c);
            Check(said.Count == 1 && (await Rows(c, "SELECT count(*) FROM companion_schema_version"))[0][0] is 0L, "database: not looked at again until it's due");
        }
        await Exec(conn, "DROP DATABASE IF EXISTS pdb_plugin_keeper WITH (FORCE)");
        await using (var c = await Fresh("pdb_plugin_partial"))
        {
            await Exec(c, "CREATE TABLE companion_pc_status (pc_name text)");
            var said = new List<string>();
            var keeper = new DatabaseKeeper(() => files, "plugin 2.1.0", said.Add);
            await keeper.RunIfDueAsync(c);
            await keeper.RunIfDueAsync(c);
            Check(keeper.Problem != null && keeper.Problem.Contains("will not change it") && said.Count == 1 && keeper.StatusText.StartsWith("Couldn't update the database"),
                "database: one with someone else's tables is left alone; the problem is kept for the Status page and said once: " + keeper.StatusText);
        }
        await Exec(conn, "DROP DATABASE IF EXISTS pdb_plugin_partial WITH (FORCE)");
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
              && subs[1] == new SubRow("Aki", "Maduin", "Main", 2, "Back", 2, 90, "S+-U+-C-U", 1700000000, 50, 200, 1),
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
        await MarketAgainstTheApp();
        RoutesAgainstTheApp();
        await RoutesReadFromTheDatabase(conn);
        await MarketAsksEachPlaceOnce();
        MarketWorkedOutOncePerRead();
        await MarketReadFromTheDatabase(conn);
        await ShoppingReadFromTheDatabase(conn);
        await TrendsAgainstTheApp(conn);
        await EditsAgainstTheApp(conn);
        await FightClubAgainstTheApp(conn);
        LooksAgainstTheApp();
        await DatabaseAgainstTheApp(conn);
        TrendsMathChecks();
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

        // Following the note (syncs and, since the Shopping List stage, reads too): to a scratch database standing in for
        // the new PC's, at the address in the note.
        var here = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("FLEET_TEST_DB") ?? "Host=localhost;Username=postgres;Password=postgres;Database=postgres");
        await Exec(conn, "DROP DATABASE IF EXISTS fleet_moved_check");
        await Exec(conn, "CREATE DATABASE fleet_moved_check");
        async Task<NpgsqlConnection> OpenMoved(string host, int port)
        {
            var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(here.ConnectionString) { Host = host, Port = port, Database = "fleet_moved_check", Pooling = false }.ConnectionString);
            await c.OpenAsync();
            return c;
        }
        async Task<NpgsqlConnection> OpenHere()
        {
            // The tests' own schema, where the move table is.
            var c = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(here.ConnectionString) { Pooling = false, SearchPath = Schema }.ConnectionString);
            await c.OpenAsync();
            return c;
        }
        await Exec(conn, "UPDATE companion_database_move SET state = 'moved', host = @h, port = @p".Replace("@h", $"'{here.Host}'").Replace("@p", here.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        var movedTo = new List<string>();
        await using (var followed = await DatabaseMove.FollowAsync(await OpenHere(), OpenMoved, (h, p) => movedTo.Add($"{h}:{p}")))
            Check(Show(await Rows(followed, "SELECT current_database()")) == "fleet_moved_check" && movedTo.SequenceEqual(new[] { $"{here.Host}:{here.Port}" }),
                "a moved database is followed to its new address, which is saved once it answers (and the new one, with no note, is used)");
        await Exec(conn, "UPDATE companion_database_move SET state = 'pausing', updated_at = now()");
        Exception? waited = null;
        try { await (await DatabaseMove.FollowAsync(await OpenHere(), OpenMoved, (_, _) => { })).DisposeAsync(); } catch (Exception ex) { waited = ex; }
        Check(waited is DatabaseMove.WaitException, "while the app copies the database, nothing is opened (wait and try again)");
        await Exec(conn, "UPDATE companion_database_move SET state = 'moved'");
        Exception? unreachable = null;
        var saved = false;
        try { await (await DatabaseMove.FollowAsync(await OpenHere(), (_, _) => throw new NpgsqlException("connection refused"), (_, _) => saved = true)).DisposeAsync(); }
        catch (Exception ex) { unreachable = ex; }
        Check(unreachable is DatabaseMove.UnreachableException && !saved, "a new address that doesn't answer is said plainly and not saved");

        // The in-game pages read through the same opening, so a PC that only reads (syncing off) switches too.
        var opened = 0;
        var reader = new FleetReader(() => here.ConnectionString, null, async () =>
        {
            opened++;
            return await DatabaseMove.FollowAsync(await OpenHere(), OpenMoved, (_, _) => { });
        });
        reader.ReadIfDue(everything: true);
        for (var w = 0; w < 200 && reader.Busy; w++) await Task.Delay(50);
        Check(opened == 1 && reader.Subs.Error != null && !reader.Subs.Error.StartsWith("Could not reach"),
            "reads open the database through the move-following opening (here the moved database, which has no fleet views yet): " + reader.Subs.Error);
        await Exec(conn, "UPDATE companion_database_move SET state = 'pausing', updated_at = now()");
        var pausedReader = new FleetReader(() => here.ConnectionString, null, async () => await DatabaseMove.FollowAsync(await OpenHere(), OpenMoved, (_, _) => { }));
        pausedReader.ReadIfDue(everything: true);
        for (var w = 0; w < 200 && pausedReader.Busy; w++) await Task.Delay(50);
        Check(pausedReader.Subs.Error != null && pausedReader.Subs.Error.Contains("being moved to another PC"),
            "while the database is being moved, the pages say so (and keep what they read before): " + pausedReader.Subs.Error);
        await Exec(conn, "DROP DATABASE fleet_moved_check WITH (FORCE)");
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
