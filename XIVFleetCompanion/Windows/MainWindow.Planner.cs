using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;

namespace XIVFleetCompanion.Windows;

// Sub Calculator → Planner, as the app's Planner page: pick a build and a route (or start from one of your subs with
// the sub picker), see its stats, voyage, the tiers each sector reaches, the loot it can bring back, and its gil,
// experience and repair cost a day. Worked out by SubCalculator from the game's files and SubCalculatorData.
public partial class MainWindow
{
    private static readonly string[] PlannerSlots = { "Hull", "Stern", "Bow", "Bridge" };
    private static readonly string[] PlannerLetters = { "S", "U", "W", "C", "Y", "S+", "U+", "W+", "C+", "Y+" };
    private static readonly Dictionary<string, string> PlannerClassNames = new() { ["S"] = "Shark", ["U"] = "Unkiu", ["W"] = "Whale", ["C"] = "Coelacanth", ["Y"] = "Syldra" };
    private static string PlannerPartName(string letter) => letter.EndsWith('+') ? $"Modified {PlannerClassNames[letter[..1]]}" : PlannerClassNames[letter];

    private int plannerRank = 100;
    private readonly string[] plannerParts = { "W+", "C+", "W+", "C+" };
    private int? plannerMap;
    private readonly List<int> plannerRoute = new();
    private int plannerAdding;
    private (long Cid, string SubName)? plannerChosen;
    private string plannerSearch = "";
    private readonly HashSet<string> plannerFolded = new();
    private string plannerPlanKey = "";
    private SubCalculator.Plan? plannerPlan;
    private SubStats? plannerStats;
    // Live Market Prices: the scope and the world to price on (the chosen sub's, else this one, else the most common).
    private string plannerPriceScope = "dc";
    // A world, data center or region picked for each scope (loot can be moved to a character elsewhere and sold there);
    // none picked: the chosen sub's own (else the most common world's). Every place Universalis knows, read once.
    private readonly Dictionary<string, string> plannerPricePicked = new();
    private (long, string)? plannerPricePickedFor;
    private volatile List<(string Name, string Dc, string Region)>? plannerPlaces;
    private Task? plannerPlacesWork;
    // Set from the background as one object, so the page never sees half of an answer. Place: "scope|name".
    private sealed record PlannerMarketAnswer(string World, HashSet<int> Ids, WorldMarket? Market, string? Error);
    private volatile PlannerMarketAnswer? plannerMarket;
    private Task? plannerMarketWork;
    private Dictionary<int, string> plannerNotes = new();
    private Func<int, double> plannerPrice = _ => 0; // the price each used for the plan (market or vendor)

    // How often an item comes, as a chance per voyage in words (as the app's plannerOften).
    private static string PlannerOften(double chance)
    {
        if (!(chance > 0)) return "—";
        if (chance >= 0.95) return "nearly every voyage";
        if (chance >= 0.45) return $"about {Math.Floor(chance * 10 + 0.5):0} in 10 voyages";
        return $"about 1 in {Math.Floor(1 / chance + 0.5).ToString("N0", CultureInfo.CurrentCulture)} voyages";
    }
    private static readonly (string Key, string Label)[] PlannerScopes = { ("world", "World"), ("dc", "Data center"), ("region", "Region") };

    // The game's sectors for the Planner: sectors by id (not starting points; named), starting points, maps with sectors.
    private sealed record PlannerGame(SubGameData Game, Dictionary<int, SubSector> Sectors, List<SubSector> Starts, List<(int Id, string Name)> Maps,
        Dictionary<int, SubRank> Ranks, Dictionary<(int, int), SubPartStat> Parts, int MaxRank);
    private PlannerGame? plannerGame;

    private PlannerGame? PlannerGameData()
    {
        if (plannerGame != null) return plannerGame;
        var game = GameNames.SubGame();
        if (game == null) return null;
        var sectors = game.Sectors.Where(s => !s.Starting && s.Id > 0 && s.Name.Length > 0).ToDictionary(s => s.Id);
        var maps = game.MapNames.Where(m => m.Value.Length > 0 && sectors.Values.Any(s => s.Map == m.Key)).OrderBy(m => m.Key).Select(m => (m.Key, m.Value)).ToList();
        var ranks = game.Ranks.ToDictionary(r => r.Id);
        var parts = game.Parts.GroupBy(p => (p.Class, p.Slot)).ToDictionary(g => g.Key, g => g.First());
        var maxRank = Math.Max(1, game.Ranks.Where(r => r.ExpToNext > 0).Select(r => r.Id + 1).DefaultIfEmpty(1).Max());
        return plannerGame = new PlannerGame(game, sectors, game.Sectors.Where(s => s.Starting).ToList(), maps, ranks, parts, maxRank);
    }

    // "Open in Planner" on the Subs and Route Optimization pages: load that sub, then show the Planner.
    private void OpenInPlanner(long cid, string subName)
    {
        PlannerLoad(cid, subName);
        shown = Tab.Planner;
    }

    private void PlannerLoad(long cid, string subName)
    {
        var sub = reader.Subs.Data?.FirstOrDefault(s => s.Cid == cid && s.SubName == subName);
        if (sub == null) return;
        plannerChosen = (cid, subName);
        var letters = sub.Build.Split('-');
        if (letters.Length == 4 && letters.All(l => SubRoutes.PartClass.ContainsKey(l))) for (var i = 0; i < 4; i++) plannerParts[i] = letters[i];
        plannerRank = sub.Rank;
        plannerRoute.Clear();
        if (RouteOf(cid, subName) is { } r)
        {
            plannerRoute.AddRange(r.Sectors.Take(5));
            if (PlannerGameData() is { } g && plannerRoute.Count > 0 && g.Sectors.TryGetValue(plannerRoute[0], out var first)) plannerMap = first.Map;
        }
    }

    // Where a sub is: out until a time (the day too when it isn't today), back waiting to be collected, or in port.
    private (string Text, Vector4? Color) PlannerWhere(long returnTime, long now)
    {
        if (returnTime <= 0) return ("In port", Dim);
        if (returnTime <= now) return ("Back, waiting", Green);
        var at = DateTimeOffset.FromUnixTimeSeconds(returnTime).LocalDateTime;
        var time = at.ToString("t", CultureInfo.CurrentCulture);
        return ($"At sea until {(at.Date == DateTime.Today ? time : $"{at.ToString("ddd", CultureInfo.CurrentCulture)} {time}")}", null);
    }

    // Choosing one of your subs: a panel grouped by account, then character (world, Free Company), each group folding
    // away, with a search across character, world, account, Free Company and sub name; each row its rank, build, route
    // and where it is now.
    // id: the page's own name for it (the Planner's and the Build Finder's are separate); onChoose: what a chosen sub does.
    private void SubPicker(string id, (long Cid, string SubName)? chosenKey, Action<SubRow> onChoose)
    {
        var subs = reader.Subs.Data ?? new List<SubRow>();
        var chosen = chosenKey is { } pc ? subs.FirstOrDefault(s => s.Cid == pc.Cid && s.SubName == pc.SubName) : null;
        // A dropdown, so ImGui draws its arrow as on every other dropdown (the game's font has no "▾"); its panel wider
        // than the box, for the subs' details.
        var scale = ImGuiHelpers.GlobalScale;
        var label = subs.Count == 0 ? "No subs synced yet" : chosen != null ? $"{chosen.SubName} · {chosen.Character}" : "Choose one of your subs…";
        ImGui.SetNextItemWidth(Math.Max(220 * scale, ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + 16 * scale));
        if (subs.Count == 0)
        {
            using (ImRaii.Disabled())
            using (ImRaii.Combo($"##{id}Picker", label)) { }
            return;
        }
        ImGui.SetNextWindowSizeConstraints(new Vector2(580 * scale, 0), new Vector2(float.MaxValue, float.MaxValue));
        using var popup = ImRaii.Combo($"##{id}Picker", label, ImGuiComboFlags.HeightLargest);
        if (!popup) return;
        ImGui.SetNextItemWidth(560 * scale);
        if (ImGui.IsWindowAppearing()) ImGui.SetKeyboardFocusHere();
        ImGui.InputTextWithHint("##plannerSearch", "Search character, world, account, FC or sub", ref plannerSearch, 64);
        var fcOf = (reader.Roster.Data ?? new List<RosterRow>()).GroupBy(r => r.Cid).ToDictionary(g => g.Key, g => g.First().FcName ?? "");
        var searching = plannerSearch.Trim().Length > 0;
        var shownSubs = subs.Where(s => Matches(plannerSearch, s.SubName, s.Character, s.World, s.Account, fcOf.GetValueOrDefault(s.Cid))).ToList();
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var list = ImRaii.Child("##plannerPickerList", new Vector2(560 * scale, 420 * scale), false);
        if (!list) return;
        if (shownSubs.Count == 0) { Colored(Dim, $"No sub matches “{plannerSearch}”."); return; }
        // Accounts in name order (no label last), characters in the roster's order as the Subs page has them.
        var accounts = shownSubs.GroupBy(s => s.Account.Length > 0 ? s.Account : "No account label")
            .OrderBy(g => g.Key == "No account label").ThenBy(g => g.Key, StringComparer.CurrentCulture);
        foreach (var account in accounts)
        {
            var aKey = $"a|{account.Key}";
            if (searching) ImGui.SetNextItemOpen(true);
            else ImGui.SetNextItemOpen(!plannerFolded.Contains(aKey));
            var aOpen = ImGui.TreeNodeEx($"{account.Key} ({account.Count()})##{aKey}", ImGuiTreeNodeFlags.SpanAvailWidth);
            if (!searching && ImGui.IsItemToggledOpen()) { if (!plannerFolded.Remove(aKey)) plannerFolded.Add(aKey); }
            if (!aOpen) continue;
            foreach (var character in account.GroupBy(s => s.Cid))
            {
                var first = character.First();
                var cKey = $"c|{character.Key}";
                var fc = fcOf.GetValueOrDefault(character.Key) ?? "";
                if (searching) ImGui.SetNextItemOpen(true);
                else ImGui.SetNextItemOpen(!plannerFolded.Contains(cKey));
                var cOpen = ImGui.TreeNodeEx($"{first.Character} ({first.World}){(fc.Length > 0 ? $" · {fc}" : "")}##{cKey}", ImGuiTreeNodeFlags.SpanAvailWidth);
                if (!searching && ImGui.IsItemToggledOpen()) { if (!plannerFolded.Remove(cKey)) plannerFolded.Add(cKey); }
                if (!cOpen) continue;
                using (var table = ImRaii.Table($"##pickerSubs{character.Key}", 5, ImGuiTableFlags.SizingStretchProp))
                {
                    if (table)
                    {
                        foreach (var s in character)
                        {
                            ImGui.TableNextRow();
                            ImGui.TableNextColumn();
                            var isChosen = chosen != null && chosen.Cid == s.Cid && chosen.SubName == s.SubName;
                            if (ImGui.Selectable($"{s.SubName}##pick{s.Cid}{s.SubName}", isChosen, ImGuiSelectableFlags.SpanAllColumns))
                            {
                                onChoose(s);
                                ImGui.CloseCurrentPopup();
                            }
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted($"Rank {s.Rank}");
                            ImGui.TableNextColumn();
                            ImGui.TextUnformatted(s.Build.Replace("-", ""));
                            ImGui.TableNextColumn();
                            var route = RouteOf(s.Cid, s.SubName)?.CurrentRoute ?? "";
                            if (route.Length > 0) ImGui.TextUnformatted(route); else Colored(Dim, "no route");
                            ImGui.TableNextColumn();
                            var (text, color) = PlannerWhere(s.ReturnTime, now);
                            Tinted(color, text);
                        }
                    }
                }
                ImGui.TreePop();
            }
            ImGui.TreePop();
        }
    }

    private static string PlannerN(double? v, int digits = 0) => v is double x ? x.ToString($"N{digits}", CultureInfo.CurrentCulture) : "—";
    private static string PlannerHours(double h) => $"{Math.Floor(h)}h {Math.Round(h % 1 * 60):00}m";
    private static string PlannerRange(SubCalculator.Breakpoint bp) => bp.Low == bp.High ? PlannerN(bp.Low) : $"{PlannerN(bp.Low)}–{PlannerN(bp.High)}";

    // A result as a coloured word: sure (green), only by one source's figure (amber), not reached (red).
    private void PlannerPill(string sure, string text) => Colored(sure == "yes" ? Green : sure == "maybe" ? Amber : Red, text);

    private void DrawPlanner()
    {
        reader.ReadIfDue(withLists: true); // the subs to choose from, and their Free Companies
        var g = PlannerGameData();
        if (g == null) { Ui.Wrapped("The Planner needs the game's sub data, which couldn't be read."); return; }
        var data = SubCalculatorData.Shipped();
        plannerRank = Math.Clamp(plannerRank, 1, g.MaxRank);
        plannerMap ??= g.Maps.Count > 0 ? g.Maps[^1].Id : null;

        Ui.Text("Start from one of your subs");
        ImGui.SameLine();
        SubPicker("planner", plannerChosen, s => PlannerLoad(s.Cid, s.SubName));
        if (plannerChosen != null)
        {
            ImGui.SameLine();
            Colored(Dim, "Change anything below to try other builds; your sub isn't changed.");
        }
        ImGui.Spacing();

        // The build and the route side by side.
        var scale = ImGuiHelpers.GlobalScale;
        using (var top = ImRaii.Table("##plannerTop", 2, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.PadOuterX))
        {
            if (top)
            {
                ImGui.TableNextColumn();
                Heading("Build");
                ImGui.SetNextItemWidth(120 * scale);
                var rank = plannerRank;
                if (ImGui.InputInt("Rank##plannerRank", ref rank)) plannerRank = Math.Clamp(rank, 1, g.MaxRank);
                for (var i = 0; i < 4; i++)
                {
                    ImGui.SetNextItemWidth(170 * scale);
                    using var combo = ImRaii.Combo($"{PlannerSlots[i]}##plannerPart{i}", PlannerPartName(plannerParts[i]));
                    if (!combo) continue;
                    foreach (var l in PlannerLetters)
                        if (ImGui.Selectable(PlannerPartName(l), plannerParts[i] == l)) plannerParts[i] = l;
                }
                plannerStats = SubRoutes.BuildStats(plannerRank, plannerParts, g.Ranks, g.Parts);
                ImGui.Spacing();
                if (plannerStats is { } st)
                    Runs(("Surveillance ", Dim), (PlannerN(st.Surveillance), null), ("  ·  Retrieval ", Dim), (PlannerN(st.Retrieval), null),
                        ("  ·  Speed ", Dim), (PlannerN(st.Speed), null), ("  ·  Range ", Dim), (PlannerN(st.Range), null), ("  ·  Favor ", Dim), (PlannerN(st.Favor), null));
                else Colored(Amber, "This rank or one of these parts isn't in the game data.");

                ImGui.TableNextColumn();
                Heading($"Route ({plannerRoute.Count} of 5 sectors)");
                ImGui.SetNextItemWidth(220 * scale);
                var mapName = g.Maps.FirstOrDefault(m => m.Id == plannerMap).Name ?? "";
                using (var combo = ImRaii.Combo("Map##plannerMap", mapName))
                {
                    if (combo)
                        foreach (var m in g.Maps)
                            if (ImGui.Selectable(m.Name, m.Id == plannerMap)) { plannerMap = m.Id; plannerRoute.Clear(); }
                }
                var onMap = g.Sectors.Values.Where(s => s.Map == plannerMap && !plannerRoute.Contains(s.Id)).OrderBy(s => s.Id).ToList();
                if (!onMap.Any(s => s.Id == plannerAdding)) plannerAdding = onMap.FirstOrDefault()?.Id ?? 0;
                string SectorLabel(SubSector s) => $"{s.Location}: {s.Name} (rank {s.RankReq}){(data.Find(s.Id) == null ? ", no loot data yet" : "")}";
                using (ImRaii.Disabled(plannerRoute.Count >= 5 || onMap.Count == 0))
                {
                    ImGui.SetNextItemWidth(300 * scale);
                    using (var combo = ImRaii.Combo("##plannerAdd", onMap.FirstOrDefault(s => s.Id == plannerAdding) is { } a ? SectorLabel(a) : ""))
                    {
                        if (combo)
                            foreach (var s in onMap)
                                if (ImGui.Selectable($"{SectorLabel(s)}##add{s.Id}", s.Id == plannerAdding)) plannerAdding = s.Id;
                    }
                    ImGui.SameLine();
                    if (ImGui.Button("Add##plannerAddButton") && plannerAdding > 0 && plannerRoute.Count < 5) plannerRoute.Add(plannerAdding);
                }
                if (plannerRoute.Count == 0) Colored(Dim, "Add up to 5 sectors.");
                for (var i = 0; i < plannerRoute.Count; i++)
                {
                    var id = plannerRoute[i];
                    var s = g.Sectors.GetValueOrDefault(id);
                    Colored(Dim, $"{i + 1}");
                    ImGui.SameLine();
                    Ui.Text(s != null ? $"{s.Location}: {s.Name}" : $"Sector {id}");
                    ImGui.SameLine();
                    if (ImGui.SmallButton($"×##plannerRemove{id}")) { plannerRoute.RemoveAt(i); break; }
                    Hover("Remove");
                }
                if (plannerRoute.Count > 1 && plannerStats is { } speedFrom && ImGui.Button("Best order##plannerBest"))
                {
                    var sectors = plannerRoute.Select(id => g.Sectors.GetValueOrDefault(id)).OfType<SubSector>().ToList();
                    var best = SubCalculator.BestOrder(sectors, SubRoutes.StartFor(sectors[0].Id, g.Starts), speedFrom.Speed, speedFrom.Range);
                    plannerRoute.Clear();
                    plannerRoute.AddRange(best.Select(s => s.Id));
                }
                if (plannerRoute.Count > 1) Hover("Put the sectors in the fastest order this sub's range allows");
                var tooHigh = plannerRoute.Select(id => g.Sectors.GetValueOrDefault(id)).OfType<SubSector>().Where(s => s.RankReq > plannerRank).ToList();
                if (tooHigh.Count > 0) Colored(Amber, $"Rank {plannerRank} can't go to {string.Join(", ", tooHigh.Select(s => s.Location))} yet.");
            }
        }

        // Market prices (Live Market Prices on): the route's loot asked of Universalis in the background for the place
        // picked (a world, data center or region), the chosen sub's own to start with.
        var live = reader.Market.Data?.Settings.LivePrices == true;
        var chosenSub = plannerChosen is { } pc ? reader.Subs.Data?.FirstOrDefault(x => x.Cid == pc.Cid && x.SubName == pc.SubName) : null;
        if (plannerPricePickedFor != plannerChosen) { plannerPricePicked.Clear(); plannerPricePickedFor = plannerChosen; }
        if (live && plannerPlaces == null && plannerPlacesWork == null)
            plannerPlacesWork = Task.Run(async () =>
            {
                try { plannerPlaces = (await reader.MarketPlacesAsync()).Values.OrderBy(p => p.Name, StringComparer.Ordinal).ToList(); }
                catch (Exception ex) { Plugin.Log.Warning(ex, "Planner: Universalis's worlds couldn't be read"); plannerPlaces = new(); }
            });
        var homeWorld = chosenSub?.World is { Length: > 0 } cw ? cw : (reader.Subs.Data ?? new List<SubRow>()).Where(x => x.World.Length > 0).GroupBy(x => x.World)
            .OrderByDescending(x => x.Count()).ThenBy(x => x.Key, StringComparer.Ordinal).Select(x => x.Key).FirstOrDefault();
        var home = plannerPlaces?.FirstOrDefault(p => string.Equals(p.Name, homeWorld, StringComparison.OrdinalIgnoreCase));
        string? HomeIn(string scope) => scope == "world" ? (home?.Name is { Length: > 0 } hn ? hn : homeWorld) : scope == "dc" ? home?.Dc : home?.Region;
        var place = live ? (plannerPricePicked.TryGetValue(plannerPriceScope, out var picked) ? picked : HomeIn(plannerPriceScope)) : null;
        var placeKey = place == null ? null : $"{plannerPriceScope}|{place}";
        var lootIds = plannerRoute.Select(id => data.Find(id)).OfType<SubCalculatorData.Sector>()
            .SelectMany(d => d.Pools.Values.SelectMany(p => p.Loot.Select(l => (int)l.ItemId))).ToHashSet();
        if (plannerMarketWork is { IsCompleted: true }) plannerMarketWork = null;
        if (placeKey != null && lootIds.Count > 0 && plannerMarketWork == null
            && !(plannerMarket is { } had && had.World == placeKey && (had.Error != null || lootIds.IsSubsetOf(had.Ids))))
        {
            var (key0, where, scope0, ids) = (placeKey, place!, plannerPriceScope,
                lootIds.Union(plannerMarket is { } was && was.World == placeKey ? was.Ids : Enumerable.Empty<int>()).ToHashSet());
            plannerMarketWork = Task.Run(async () =>
            {
                try { plannerMarket = new PlannerMarketAnswer(key0, ids, await reader.MarketInAsync(where, scope0, ids), null); }
                catch (Exception ex) { plannerMarket = new PlannerMarketAnswer(key0, ids, null, ex.Message); }
            });
        }
        var market = plannerMarket is { } pm && pm.World == placeKey && pm.Market != null ? pm.Market : null;
        Func<uint, double> price = id => ListingVendor((int)id);
        if (market != null)
        {
            var vendor = lootIds.ToDictionary(id => id, id => ListingVendor(id));
            var notes = new Dictionary<int, string>();
            var prices = Universalis.ScopePrices(market, plannerPriceScope, vendor, reader.Market.Data?.Live?.Exclude, notes: notes);
            plannerNotes = notes;
            price = id => prices.TryGetValue((int)id, out var p) ? p : ListingVendor((int)id);
        }
        else plannerNotes = new();
        plannerPrice = id => price((uint)id);

        // The plan, worked out again only when the build, the route, the prices or the repair kit price change.
        var kitCost = reader.Credits.Data?.RepairKitCost ?? 0;
        var key = $"{plannerRank}|{string.Join(",", plannerParts)}|{string.Join(",", plannerRoute)}|{kitCost}|{placeKey}|{(market == null ? 0 : market.GetHashCode())}";
        if (key != plannerPlanKey)
        {
            plannerPlanKey = key;
            plannerPlan = SubCalculator.PlanFor(plannerRoute.Take(5).ToList(), plannerStats, plannerParts.Select(l => SubRoutes.PartClass[l]).ToList(),
                g.Sectors, g.Starts, g.Game.Parts, data, price, kitCost);
        }
        var plan = plannerPlan;
        if (plan == null || plannerStats == null) { PlannerSource(data); return; }

        // The voyage.
        ImGui.Spacing();
        var over = plan.Range is int used && used > plannerStats.Range;
        Runs(("Range used ", Dim), ($"{PlannerN(plan.Range)} / {PlannerN(plannerStats.Range)}", over ? Red : null),
            ("   Voyage ", Dim), (PlannerHours(plan.Hours), null),
            ("   Experience ", Dim), (PlannerN(plan.ExpSure), null),
            ("   Ceruleum ", Dim), ($"{PlannerN(plan.Tanks)} tank{(plan.Tanks == 1 ? "" : "s")}", null));
        if (over) Colored(Red, $"Out of range: needs {PlannerN(plan.Range - plannerStats.Range)} more range");
        Colored(Dim, $"{(plan.ExpSure > plan.Exp ? $"{PlannerN(plan.Exp)} experience + sure bonus; " : "")}up to {PlannerN(plan.ExpBest)} with luck");
        Hover("Bonus experience, from Pepper's FFXIV Ship Guide: optimal retrieval +25% (sure once its breakpoint is met); T2 loot +25%, T3 loot +50%, " +
              "HQ materials +25% and a double dip +25% (luck); at most +100% a sector");

        // Where the prices are from: the market (World / Data center / Region of a world) or vendors.
        if (live)
        {
            ImGui.Spacing();
            Colored(Dim, "Market prices on the");
            foreach (var (k, label) in PlannerScopes)
            {
                ImGui.SameLine(0, 2 * scale);
                using (ImRaii.PushColor(ImGuiCol.Button, ImGui.GetColorU32(ImGuiCol.ButtonActive), plannerPriceScope == k))
                    if (ImGui.SmallButton($"{label}##plannerScope{k}")) plannerPriceScope = k;
            }
            // Every world, data center or region to pick from, the sub's own first.
            ImGui.SameLine();
            var own = HomeIn(plannerPriceScope);
            var whose = chosenSub != null ? $"{chosenSub.SubName}'s" : "your most common";
            var noun = PlannerScopes.First(x => x.Key == plannerPriceScope).Label.ToLowerInvariant();
            string Shown(string name) => plannerPriceScope == "region" ? Universalis.RegionLabel(name) : name;
            var options = (plannerPlaces ?? new()).Select(p => plannerPriceScope == "world" ? (p.Name, $"{p.Name} ({p.Dc})") : plannerPriceScope == "dc" ? (p.Dc, p.Dc) : (p.Region, Universalis.RegionLabel(p.Region)))
                .Where(o => !string.IsNullOrEmpty(o.Item1)).Distinct().OrderBy(o => o.Item2, StringComparer.Ordinal).ToList();
            ImGui.SetNextItemWidth(220 * scale);
            using (var combo = ImRaii.Combo("##plannerPricePlace", place == null ? "—" : place == own ? $"{Shown(place)} ({whose} {noun})" : Shown(place), ImGuiComboFlags.HeightLarge))
            {
                if (combo)
                {
                    if (own != null && ImGui.Selectable($"{Shown(own)} ({whose} {noun})##own", place == own)) plannerPricePicked.Remove(plannerPriceScope);
                    foreach (var (name, label) in options.Where(o => o.Item1 != own))
                        if (ImGui.Selectable($"{label}##place{name}", place == name)) plannerPricePicked[plannerPriceScope] = name;
                    if (options.Count == 0) Colored(Dim, plannerPlaces == null ? "Reading Universalis's list..." : "Universalis's list couldn't be read.");
                }
            }
            if (plannerNotes.Count > 0)
            {
                ImGui.SameLine(0, 12 * scale);
                Colored(Amber, $"{plannerNotes.Count} price{(plannerNotes.Count == 1 ? "" : "s")} adjusted for odd listings");
                Hover("A lone listing far below the others was skipped, or the last week's sales were used: hover a price for why.");
            }
            if (plannerMarket is { Error: { } err } failed && failed.World == placeKey) Colored(Amber, $"Universalis: {err} Vendor prices are shown.");
            else if (market?.Stale is { } stale) Colored(Amber, $"Some prices are from {stale.Since.ToLocalTime():t}: {stale.Problem}");
            else if (market == null && plannerMarketWork != null) Colored(Dim, "Getting market prices from Universalis...");
        }

        // Gil, experience and repairs, side by side.
        ImGui.Spacing();
        using (var boxes = ImRaii.Table("##plannerBoxes", 4, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.PadOuterX))
        {
            if (boxes)
            {
                ImGui.TableNextColumn();
                BoxTitle("Gil per voyage", PlannerN(plan.GilPerVoyage), Palette.Gil);
                if (market != null)
                {
                    var where = plannerPriceScope == "world" ? market.Names.World : plannerPriceScope == "dc" ? market.Names.Dc : market.Names.Region;
                    Colored(Dim, $"expected, at market prices ({PlannerScopes.First(x => x.Key == plannerPriceScope).Label}{(!string.IsNullOrEmpty(where) ? $": {where}" : "")})");
                    Hover("Items not on the market board at what a vendor pays; salvage at the Salvage page's prices.");
                }
                else
                {
                    Colored(Dim, "expected, at vendor prices");
                    Hover("Salvage at the Salvage page's prices.");
                }
                ImGui.TableNextColumn();
                BoxTitle("Gil per day", PlannerN(plan.GilPerDay), Palette.Gil);
                Colored(Dim, $"{PlannerN(plan.VoyagesPerDay, 2)} voyages a day");
                Hover("Sent out again as soon as it's back.");
                ImGui.TableNextColumn();
                BoxTitle("Experience per day", PlannerN(plan.ExpSure * plan.VoyagesPerDay), null);
                Colored(Dim, $"up to {PlannerN(plan.ExpBest * plan.VoyagesPerDay)} with luck");
                ImGui.TableNextColumn();
                BoxTitle("Repairs", plan.KitsPerVoyage is double k ? $"{PlannerN(k * plan.VoyagesPerDay, 1)} kits a day" : "—", null);
                var lasts = plan.Wear.All(w => w != null) ? plan.Wear.Min(w => w!.VoyagesLasting ?? int.MaxValue) : (int?)null;
                Colored(Dim, (plan.RepairGilPerDay is > 0 ? $"{PlannerN(plan.RepairGilPerDay)} gil a day; " : "") +
                             $"parts last {(lasts is int n && n != int.MaxValue ? $"{n} voyages" : "—")}");
                Hover(plan.RepairGilPerDay is > 0 ? $"At {PlannerN(kitCost)} gil a kit." : "Set the repair kit price on the Supplies page for the gil.");
            }
        }

        // What this build reaches on each sector, and the loot it can bring back: tables as on every page (columns to
        // size, move, sort and choose).
        HeadingRuns(("What this build reaches", null), ("   amber: the sources differ, so it might not", Dim));
        PlannerReachTable.DrawControls();
        PlannerReachTable.Draw(plan.Sectors, fitRows: true);
        HeadingRuns(("Possible loot", null), ("   greyed: tiers this build can't count on", Dim));
        PlannerLootTable.DrawControls();
        PlannerLootTable.Draw(plan.Loot, fitRows: true);
        PlannerSource(data);
    }

    private FleetTable<SubCalculator.SectorPlan>? plannerReachTable;
    private FleetTable<SubCalculator.RouteLoot>? plannerLootTable;

    // A cell of a sector without XIVStats data: a soft dash.
    private bool PlannerNoData(SubCalculator.SectorPlan sp)
    {
        if (sp.Data != null && sp.Result != null && sp.Bonus != null) return false;
        Colored(Dim, "—");
        return true;
    }

    private FleetTable<SubCalculator.SectorPlan> PlannerReachTable => plannerReachTable ??= new FleetTable<SubCalculator.SectorPlan>("plannerReach", plugin.Configuration,
        new List<FleetColumn<SubCalculator.SectorPlan>>
    {
        new("sector", "Sector", sp => Ui.Text($"{sp.Sector.Location}: {sp.Sector.Name}"), sp => sp.Sector.Location, Locked: true),
        new("tier", "Loot tier", sp =>
        {
            if (sp.Result == null) { Colored(Dim, "No XIVStats data for this sector yet."); return; }
            var r = sp.Result;
            PlannerPill(r.TierSure, $"Tier {r.Tier}");
            if (r.Tier < 3) Colored(Dim, $"{PlannerN(r.Need.T3)} more surveillance for T3");
            else if (r.TierSure == "maybe") Colored(Amber, $"{PlannerN(r.Need.T3)} more to be sure");
        }, sp => sp.Result?.Tier ?? -1, sp => sp.Result == null),
        new("retrieval", "Retrieval", sp =>
        {
            if (PlannerNoData(sp)) return;
            var r = sp.Result!;
            PlannerPill(r.Retrieval == "optimal" ? r.RetrievalSure : r.Retrieval == "normal" ? "maybe" : "no", CultureInfo.InvariantCulture.TextInfo.ToTitleCase(r.Retrieval));
            if (r.Retrieval != "optimal" || r.RetrievalSure == "maybe") Colored(Dim, $"{PlannerN(r.Need.Optimal)} more retrieval for optimal");
        }, sp => sp.Result == null ? -1 : Array.IndexOf(new[] { "poor", "normal", "optimal" }, sp.Result.Retrieval), sp => sp.Result == null),
        new("doubleDip", "Double dip", sp =>
        {
            if (PlannerNoData(sp)) return;
            var r = sp.Result!;
            PlannerPill(r.DoubleDip, r.DoubleDip == "yes" ? "Yes" : r.DoubleDip == "maybe" ? "Maybe" : "No");
            if (r.DoubleDip != "yes") Colored(Dim, $"{PlannerN(r.Need.Favor)} more favor");
        }, sp => sp.Result == null ? -1 : Array.IndexOf(new[] { "no", "maybe", "yes" }, sp.Result.DoubleDip), sp => sp.Result == null),
        new("tiers", "T2 / T3 at", sp => { if (!PlannerNoData(sp)) TableCells.Right($"{PlannerRange(SubCalculator.BreakpointOf(sp.Data!, 0))} / {PlannerRange(SubCalculator.BreakpointOf(sp.Data!, 1))}"); },
            sp => sp.Data == null ? -1 : SubCalculator.BreakpointOf(sp.Data, 1).High, sp => sp.Data == null),
        new("retrievals", "Normal / Optimal at", sp => { if (!PlannerNoData(sp)) TableCells.Right($"{PlannerRange(SubCalculator.BreakpointOf(sp.Data!, 2))} / {PlannerRange(SubCalculator.BreakpointOf(sp.Data!, 3))}"); },
            sp => sp.Data == null ? -1 : SubCalculator.BreakpointOf(sp.Data, 3).High, sp => sp.Data == null),
        new("favor", "Favor at", sp => { if (!PlannerNoData(sp)) TableCells.Right(PlannerRange(SubCalculator.BreakpointOf(sp.Data!, 4))); },
            sp => sp.Data == null ? -1 : SubCalculator.BreakpointOf(sp.Data, 4).High, sp => sp.Data == null),
        new("bonus", "Bonus exp", sp =>
        {
            if (PlannerNoData(sp)) return;
            TableCells.Right($"{(sp.Bonus!.Sure > 0 ? $"+{sp.Bonus.Sure}% sure, " : "")}up to +{sp.Bonus.Best}%");
            Hover($"{PlannerN(sp.Sector.ExpReward)} experience before bonuses");
        }, sp => sp.Bonus?.Best ?? -1, sp => sp.Bonus == null),
    });

    // A loot row this build can't count on is greyed.
    private void PlannerLootText(SubCalculator.RouteLoot l, string text, bool right = false, Vector4? color = null)
    {
        var shownColor = l.PerVoyage > 0 ? color : Dim;
        if (right) TableCells.Right(text, shownColor);
        else Tinted(shownColor, text);
    }

    private FleetTable<SubCalculator.RouteLoot> PlannerLootTable => plannerLootTable ??= new FleetTable<SubCalculator.RouteLoot>("plannerLoot", plugin.Configuration,
        new List<FleetColumn<SubCalculator.RouteLoot>>
    {
        new("item", "Item", l => PlannerLootText(l, ItemName((int)l.ItemId, false)), l => ItemName((int)l.ItemId, false), Locked: true),
        new("tier", "Tier", l => PlannerLootText(l, $"T{l.Tier}"), l => l.Tier),
        new("sectors", "Sectors", l => PlannerLootText(l, l.Sectors.Count > 0 ? string.Join(", ", l.Sectors) : "—"), l => string.Join(",", l.Sectors)),
        new("amount", "Amount each time", l => PlannerLootText(l, l.Low == l.High ? $"{l.Low}" : $"{l.Low}–{l.High}", true), l => l.High),
        // How often it comes at all, in words ("about 1 in 15 voyages"): a rare item's gil per voyage is an average.
        new("often", "How often", l =>
        {
            PlannerLootText(l, l.PerVoyage > 0 ? PlannerOften(l.Chance) : "—");
            if (l.PerVoyage > 0) Hover($"{PlannerN(l.Chance * 100, 1)}% of voyages");
        }, l => l.Chance),
        new("perVoyage", "Per voyage", l => PlannerLootText(l, l.PerVoyage > 0 ? PlannerN(l.PerVoyage, 2) : "—", true), l => l.PerVoyage),
        // One of it at the price used (market or vendor): something to aim for. Amber where the market price was adjusted
        // (a lone low listing skipped, or recent sales used): the hover says why.
        new("price", "Price each", l =>
        {
            var note = plannerNotes.GetValueOrDefault((int)l.ItemId);
            var each = l.PerVoyage > 0 ? l.Gil / l.PerVoyage : plannerPrice((int)l.ItemId);
            PlannerLootText(l, each > 0 ? PlannerN(each) : "—", true, note != null ? Amber : Palette.Gil);
            if (note != null) Hover(note);
        }, l => plannerPrice((int)l.ItemId)),
        new("gil", "Gil per voyage", l =>
        {
            PlannerLootText(l, l.Gil > 0 ? PlannerN(l.Gil) : "—", true, Palette.Gil);
            Hover("On average: how many a voyage × the price each");
        }, l => l.Gil),
    });

    // Where the data comes from (as the app's Planner foot).
    private void PlannerSource(SubCalculatorData.Data data)
    {
        ImGui.Spacing();
        var updated = data.About.XivstatsUpdated is DateTime u ? $" (updated {u.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)})" : "";
        using (ImRaii.PushColor(ImGuiCol.Text, Dim))
            Ui.Wrapped($"Loot and breakpoints: XIVStats{updated}, checked against Pepper's FFXIV Ship Guide. Range, voyage time and wear as Submarine Tracker " +
                       "works them out. Expected loot counts a breakpoint only when both sources agree it's met. Bonus experience as Pepper's guide gives it.");
    }
}
