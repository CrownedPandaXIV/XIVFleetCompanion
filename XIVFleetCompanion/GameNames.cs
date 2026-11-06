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
