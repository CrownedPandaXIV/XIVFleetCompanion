using System;
using System.Collections.Generic;
using Lumina.Excel.Sheets;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Names and numbers from the game's own files (2.x): a class's abbreviation, a venture's name, an item's name and
    /// level. The database's views give the game's numbers, never names, so they work on a database without the app's
    /// game data; the windows look the names up here. Kept once found, since they don't change while the game runs.
    /// </summary>
    public static class GameNames
    {
        private static readonly Dictionary<int, string> Jobs = new();
        private static readonly Dictionary<int, string> Ventures = new();
        private static readonly Dictionary<int, string> Items = new();
        private static readonly Dictionary<int, int?> ItemLevels = new();
        private static readonly Dictionary<int, string> Worlds = new();
        private static readonly Dictionary<int, double> VendorPrices = new();

        private static SubGameData? subGame;
        private static ShoppingGame? shopGame;
        private static readonly object ShopLock = new();
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<int, string?> ShopNames = new();

        /// <summary>What the Shopping List is worked out from: the recipes (Recipe), how the FC workshop builds sub parts
        /// (CompanyCraftSequence, CompanyCraftPart, CompanyCraftProcess, CompanyCraftSupplyItem), what gil shops sell
        /// (GilShopItem) at the item's vendor price (Item's PriceMid), and item names. Read once, on whichever thread asks
        /// first (the Shopping List works in the background); null when they can't be read.</summary>
        public static ShoppingGame? ShopGame()
        {
            lock (ShopLock)
            {
                if (shopGame != null) return shopGame;
                try
                {
                    var recipes = new List<ShopRecipeRow>();
                    foreach (var r in Plugin.DataManager.GetExcelSheet<Recipe>())
                    {
                        if (r.ItemResult.RowId == 0) continue;
                        var ingredients = new List<(int, int)>();
                        for (var i = 0; i < r.Ingredient.Count && i < r.AmountIngredient.Count; i++)
                            ingredients.Add(((int)r.Ingredient[i].RowId, r.AmountIngredient[i]));
                        recipes.Add(new ShopRecipeRow((int)r.RowId, (int)r.ItemResult.RowId, r.AmountResult, (int)r.CraftType.RowId, ingredients));
                    }
                    var sequences = new List<ShopSequence>();
                    foreach (var q in Plugin.DataManager.GetExcelSheet<CompanyCraftSequence>())
                    {
                        if (q.ResultItem.RowId == 0) continue;
                        var parts = new List<int>();
                        for (var i = 0; i < q.CompanyCraftPart.Count; i++) parts.Add((int)q.CompanyCraftPart[i].RowId);
                        sequences.Add(new ShopSequence((int)q.RowId, (int)q.ResultItem.RowId, parts));
                    }
                    var craftParts = new List<(int, List<int>)>();
                    foreach (var c in Plugin.DataManager.GetExcelSheet<CompanyCraftPart>())
                    {
                        var procs = new List<int>();
                        for (var i = 0; i < c.CompanyCraftProcess.Count; i++) procs.Add((int)c.CompanyCraftProcess[i].RowId);
                        craftParts.Add(((int)c.RowId, procs));
                    }
                    var processes = new List<ShopProcess>();
                    foreach (var p in Plugin.DataManager.GetExcelSheet<CompanyCraftProcess>())
                    {
                        var supply = new List<int>();
                        var perSet = new List<int>();
                        var sets = new List<int>();
                        for (var i = 0; i < p.SupplyItem.Count; i++)
                        {
                            supply.Add((int)p.SupplyItem[i].RowId);
                            perSet.Add(i < p.SetQuantity.Count ? p.SetQuantity[i] : 0);
                            sets.Add(i < p.SetsRequired.Count ? p.SetsRequired[i] : 0);
                        }
                        processes.Add(new ShopProcess((int)p.RowId, supply, perSet, sets));
                    }
                    var supplyItems = new List<(int, int)>();
                    foreach (var x in Plugin.DataManager.GetExcelSheet<CompanyCraftSupplyItem>()) supplyItems.Add(((int)x.RowId, (int)x.Item.RowId));
                    var items = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>();
                    var vendor = new Dictionary<int, double>();
                    foreach (var rows in Plugin.DataManager.GetSubrowExcelSheet<GilShopItem>())
                        foreach (var g in rows)
                        {
                            var id = (int)g.Item.RowId;
                            if (id <= 0 || vendor.ContainsKey(id)) continue;
                            if (items.GetRowOrDefault((uint)id) is { } item && item.PriceMid > 0) vendor[id] = item.PriceMid;
                        }
                    shopGame = new ShoppingGame(recipes, sequences, craftParts, processes, supplyItems, vendor, id => ShopNames.GetOrAdd(id, key =>
                    {
                        try
                        {
                            return Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault((uint)key) is { } row ? row.Name.ExtractText() : null;
                        }
                        catch { return null; }
                    }));
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Could not read the crafting data from the game's files");
                }
                return shopGame;
            }
        }

        /// <summary>The sectors, maps, ranks and parts the routes are worked out from (SubmarineExploration, SubmarineMap,
        /// SubmarineRank, SubmarinePart), read once; null when they can't be read.</summary>
        public static SubGameData? SubGame()
        {
            if (subGame != null) return subGame;
            try
            {
                var sectors = new List<SubSector>();
                foreach (var r in Plugin.DataManager.GetExcelSheet<SubmarineExploration>())
                    sectors.Add(new SubSector((int)r.RowId, r.Location.ExtractText(), (int)r.Map.RowId, r.X, r.Y, r.Z, r.ExpReward, r.SurveyDurationmin, r.StartingPoint));
                var maps = new Dictionary<int, string>();
                foreach (var r in Plugin.DataManager.GetExcelSheet<SubmarineMap>()) maps[(int)r.RowId] = r.Name.ExtractText();
                var ranks = new List<SubRank>();
                foreach (var r in Plugin.DataManager.GetExcelSheet<SubmarineRank>()) ranks.Add(new SubRank((int)r.RowId, r.SpeedBonus, r.RangeBonus, r.ExpToNext));
                var parts = new List<SubPartStat>();
                foreach (var r in Plugin.DataManager.GetExcelSheet<SubmarinePart>()) parts.Add(new SubPartStat(r.Class, r.Slot, r.Speed, r.Range));
                if (sectors.Count > 0 && ranks.Count > 0 && parts.Count > 0) subGame = new SubGameData(sectors, maps, ranks, parts);
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Could not read the sea map data from the game's files");
            }
            return subGame;
        }

        /// <summary>A world's name for its number; "" when unknown.</summary>
        public static string World(int world)
        {
            if (world <= 0) return "";
            if (Worlds.TryGetValue(world, out var known)) return known;
            var name = "";
            try
            {
                var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.World>().GetRowOrDefault((uint)world);
                if (row.HasValue) name = row.Value.Name.ExtractText();
            }
            catch (Exception ex) { Plugin.Log.Debug($"Fleet Companion: world {world} — {ex.Message}"); }
            return Worlds[world] = name;
        }

        /// <summary>"WHM" for a class number; "" for none (0) or one the game doesn't know.</summary>
        public static string Job(int job)
        {
            if (job <= 0) return "";
            if (Jobs.TryGetValue(job, out var known)) return known;
            var name = "";
            try
            {
                var row = Plugin.DataManager.GetExcelSheet<ClassJob>().GetRowOrDefault((uint)job);
                if (row.HasValue) name = row.Value.Abbreviation.ExtractText();
            }
            catch (Exception ex) { Plugin.Log.Debug($"Fleet Companion: class {job} — {ex.Message}"); }
            return Jobs[job] = name;
        }

        /// <summary>
        /// A venture's name, as the Retainers tab's Venture Type: a random venture's own name (Quick Exploration, Field
        /// Exploration...), else the item a normal venture brings back. "" when unknown.
        /// </summary>
        public static string Venture(int ventureId)
        {
            if (ventureId <= 0) return "";
            if (Ventures.TryGetValue(ventureId, out var known)) return known;
            var name = "";
            try
            {
                var task = Plugin.DataManager.GetExcelSheet<RetainerTask>().GetRowOrDefault((uint)ventureId);
                if (task.HasValue)
                {
                    var taskId = task.Value.Task.RowId;
                    if (task.Value.IsRandom)
                    {
                        var random = Plugin.DataManager.GetExcelSheet<RetainerTaskRandom>().GetRowOrDefault(taskId);
                        if (random.HasValue) name = random.Value.Name.ExtractText();
                    }
                    else
                    {
                        var normal = Plugin.DataManager.GetExcelSheet<RetainerTaskNormal>().GetRowOrDefault(taskId);
                        if (normal.HasValue) name = Item((int)normal.Value.Item.RowId);
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.Debug($"Fleet Companion: venture {ventureId} — {ex.Message}"); }
            return Ventures[ventureId] = name;
        }

        /// <summary>An item's name; "" when unknown.</summary>
        public static string Item(int itemId)
        {
            if (itemId <= 0) return "";
            if (Items.TryGetValue(itemId, out var known)) return known;
            var name = "";
            try
            {
                var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault((uint)itemId);
                if (row.HasValue) name = row.Value.Name.ExtractText();
            }
            catch (Exception ex) { Plugin.Log.Debug($"Fleet Companion: item {itemId} — {ex.Message}"); }
            return Items[itemId] = name;
        }

        /// <summary>What a vendor pays for an item (the game's own price, as the app's game data); 0 when unknown.</summary>
        public static double VendorPrice(int itemId)
        {
            if (VendorPrices.TryGetValue(itemId, out var known)) return known;
            double price = 0;
            try
            {
                var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault((uint)itemId);
                if (row.HasValue) price = row.Value.PriceLow;
            }
            catch (Exception ex) { Plugin.Log.Debug($"Fleet Companion: item {itemId} — {ex.Message}"); }
            return VendorPrices[itemId] = price;
        }

        private static readonly Dictionary<int, (int Slot, int LevelEquip, int LevelItem, uint Category)?> GearInfos = new();
        private static readonly Dictionary<(uint, string), bool?> Wearable = new();

        /// <summary>A piece of gear as a retainer's gear target checks it (FleetReader.GearCheck), for the class
        /// abbreviation wearing it; null when the game doesn't know the item.</summary>
        public static FleetReader.GearPiece? Gear(int itemId, string abbreviation)
        {
            if (!GearInfos.TryGetValue(itemId, out var info))
            {
                info = null;
                try
                {
                    var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault((uint)itemId);
                    if (row.HasValue)
                        info = ((int)row.Value.EquipSlotCategory.RowId, (int)row.Value.LevelEquip, (int)row.Value.LevelItem.RowId, row.Value.ClassJobCategory.RowId);
                }
                catch (Exception ex) { Plugin.Log.Debug($"Fleet Companion: item {itemId} — {ex.Message}"); }
                GearInfos[itemId] = info;
            }
            if (info is not { } i) return null;
            return new FleetReader.GearPiece(i.Slot, i.LevelEquip, i.LevelItem, CanWear(i.Category, abbreviation));
        }

        // Whether a class can wear an item of a ClassJobCategory: the sheet has a yes/no for each class, named by its
        // abbreviation (GLA, PLD, ...), as the app's game data. Null when unknown.
        private static bool? CanWear(uint category, string abbreviation)
        {
            if (abbreviation.Length == 0) return null;
            if (Wearable.TryGetValue((category, abbreviation), out var known)) return known;
            bool? can = null;
            try
            {
                var row = Plugin.DataManager.GetExcelSheet<ClassJobCategory>().GetRowOrDefault(category);
                var field = typeof(ClassJobCategory).GetProperty(abbreviation);
                if (row.HasValue && field?.PropertyType == typeof(bool)) can = (bool)field.GetValue(row.Value)!;
            }
            catch (Exception ex) { Plugin.Log.Debug($"Fleet Companion: class category {category} — {ex.Message}"); }
            return Wearable[(category, abbreviation)] = can;
        }

        /// <summary>An item's level (ilvl); null when the game doesn't know the item.</summary>
        public static int? ItemLevel(int itemId)
        {
            if (ItemLevels.TryGetValue(itemId, out var known)) return known;
            int? level = null;
            try
            {
                var row = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Item>().GetRowOrDefault((uint)itemId);
                if (row.HasValue) level = (int)row.Value.LevelItem.RowId;
            }
            catch (Exception ex) { Plugin.Log.Debug($"Fleet Companion: item {itemId} — {ex.Message}"); }
            return ItemLevels[itemId] = level;
        }
    }
}
