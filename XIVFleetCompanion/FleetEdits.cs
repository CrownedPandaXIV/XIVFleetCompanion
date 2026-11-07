using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Npgsql;
using System.Data.Common;

namespace XIVFleetCompanion
{
    /// <summary>
    /// 2.x "ticking things off" stage: the edits the app makes, made from the game the same way (the same rows written,
    /// the same checks, the same answers): Sub Rules (ipc/parts.js), Craft? (ipc/subs.js), gear targets
    /// (ipc/retainers.js), the Roster order (ipc/roster.js), Gil &amp; Market's unsold days and removing a PC
    /// (ipc/details.js), the Income window (ipc/income.js) and the Shopping List's ticks (ipc/shopping.js,
    /// subs/shopping-progress.js). Each answers null when done, else why not. The app's tests make the same edits and
    /// write what each leaves in the database (tests/db/edits-fixture.js), which the plugin's tests check this against.
    /// </summary>
    public static class FleetEdits
    {
        public const int MaxStaleDays = 90;
        public static readonly int[] WindowChoices = { 3, 7, 14, 30 };
        private static readonly Regex Id = new("^[0-9]{1,25}$");

        /// <summary>A Sub Rule as the app saves it: from rank, for one character (null: everyone), the slots ("": all,
        /// "Sub1,Sub3") and each part's letter ("": none).</summary>
        public sealed record Rule(long? Id, int Rank, string? CidOverride, string? SubFilter, string? Hull, string? Stern, string? Bow, string? Bridge);

        private static object Text(string? value) => string.IsNullOrEmpty(value) ? "" : value;
        private static object Cid(string? cid) => string.IsNullOrEmpty(cid) ? DBNull.Value : decimal.Parse(cid, CultureInfo.InvariantCulture);

        // PostgreSQL's numbered parameters ($1, $2...) and now(); in the file (Just this PC, see Db) the same statement
        // with named parameters and the file's time.
        private static DbCommand Command(DbConnection conn, string sql, params object?[] values)
        {
            if (!Db.IsFile(conn))
            {
                var pg = new NpgsqlCommand(sql, (NpgsqlConnection)conn);
                foreach (var v in values) pg.Parameters.Add(new NpgsqlParameter { Value = v ?? DBNull.Value });
                return pg;
            }
            var cmd = Db.Command(conn, Db.FileSql(System.Text.RegularExpressions.Regex.Replace(sql, @"\$(\d+)", "@p$1")));
            for (var i = 0; i < values.Length; i++) Db.Add(cmd, $"p{i + 1}", values[i] is DBNull ? null : values[i]);
            return cmd;
        }

        /// <summary>A new Sub Rule; its id.</summary>
        public static async Task<long> AddRule(DbConnection conn, Rule r)
        {
            await using var cmd = Command(conn, @"INSERT INTO sub_build_rules (rank, cid_override, sub_filter, hull, stern, bow, bridge, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, now()) RETURNING id", r.Rank, Cid(r.CidOverride), Text(r.SubFilter), Text(r.Hull), Text(r.Stern), Text(r.Bow), Text(r.Bridge));
            return Convert.ToInt64(await cmd.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
        }

        public static async Task UpdateRule(DbConnection conn, Rule r)
        {
            await using var cmd = Command(conn, @"UPDATE sub_build_rules
                SET rank = $1, cid_override = $2, sub_filter = $3, hull = $4, stern = $5, bow = $6, bridge = $7, updated_at = now()
                WHERE id = $8", r.Rank, Cid(r.CidOverride), Text(r.SubFilter), Text(r.Hull), Text(r.Stern), Text(r.Bow), Text(r.Bridge), (int)(r.Id ?? 0));
            await cmd.ExecuteNonQueryAsync();
        }

        public static async Task DeleteRule(DbConnection conn, long id)
        {
            await using var cmd = Command(conn, "DELETE FROM sub_build_rules WHERE id = $1", (int)id);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Craft? for one sub: follows Fight Club's builds from rank 85 (on), or its Sub Rules (off).</summary>
        public static async Task SetCraft(DbConnection conn, string cid, string subName, bool enabled)
        {
            await using var cmd = Command(conn, @"INSERT INTO sub_craft_toggle (cid, sub_name, craft_enabled, updated_at)
                VALUES ($1, $2, $3, now())
                ON CONFLICT (cid, sub_name) DO UPDATE SET craft_enabled = EXCLUDED.craft_enabled, updated_at = now()", Cid(cid), subName, enabled);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Why a gear target can't be saved (checkRetainerTarget), or null.</summary>
        public static string? CheckTarget(string retainerId, string? mode, int? value)
        {
            if (!Id.IsMatch(retainerId)) return "Invalid retainer.";
            if (mode != null && mode != "level" && mode != "ilvl") return "The target mode must be Level or ilvl.";
            if (value != null && (value < 1 || value > 999)) return "The target value must be a whole number from 1 to 999.";
            if (!string.IsNullOrEmpty(mode) && value == null) return "Enter a target value.";
            return null;
        }

        /// <summary>A retainer's gear target: by level or item level (mode, null: none), the value, and whether only crafted gear counts.</summary>
        public static async Task<string?> SaveTarget(DbConnection conn, string retainerId, string? mode, int? value, bool onlyCrafted)
        {
            if (CheckTarget(retainerId, mode, value) is { } problem) return problem;
            await using var cmd = Command(conn, @"INSERT INTO retainer_gear_targets (retainer_id, target_mode, target_value, only_crafted, updated_at)
                VALUES ($1, $2, $3, $4, now())
                ON CONFLICT (retainer_id) DO UPDATE SET target_mode = $2, target_value = $3, only_crafted = $4, updated_at = now()",
                Cid(retainerId), mode, value, onlyCrafted);
            await cmd.ExecuteNonQueryAsync();
            return null;
        }

        /// <summary>Clears a retainer's gear target (its row goes).</summary>
        public static async Task<string?> DeleteTarget(DbConnection conn, string retainerId)
        {
            if (!Id.IsMatch(retainerId)) return "Invalid retainer.";
            await using var cmd = Command(conn, "DELETE FROM retainer_gear_targets WHERE retainer_id = $1", Cid(retainerId));
            await cmd.ExecuteNonQueryAsync();
            return null;
        }

        /// <summary>The Roster order: every character's place, in one go (all or nothing).</summary>
        public static async Task SaveOrder(DbConnection conn, IReadOnlyList<string> cids)
        {
            await using var tx = await conn.BeginTransactionAsync();
            for (var i = 0; i < cids.Count; i++)
            {
                await using var cmd = Command(conn, @"INSERT INTO character_sort_order (cid, sort_index, updated_at)
                    VALUES ($1, $2, now())
                    ON CONFLICT (cid) DO UPDATE SET sort_index = EXCLUDED.sort_index, updated_at = now()", Cid(cids[i]), i);
                cmd.Transaction = tx;
                await cmd.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }

        private static async Task SaveEconomics(DbConnection conn, string key, decimal value)
        {
            await using var cmd = Command(conn, @"INSERT INTO fleet_economics_settings (setting_key, setting_value, updated_at) VALUES ($1, $2, now())
                ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value, updated_at = now()", key, value);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Gil &amp; Market: how many days up for sale counts as unsold for a while (1 to 90).</summary>
        public static async Task<string?> SaveStaleDays(DbConnection conn, double days)
        {
            if (days != Math.Floor(days) || days < 1 || days > MaxStaleDays) return $"Choose 1 to {MaxStaleDays} days.";
            await SaveEconomics(conn, "market_stale_days", (decimal)days);
            return null;
        }

        /// <summary>Income: how many days its figures cover (3, 7, 14 or 30).</summary>
        public static async Task<string?> SaveIncomeWindow(DbConnection conn, int days)
        {
            if (!WindowChoices.Contains(days)) return $"Choose {string.Join(", ", WindowChoices)} days.";
            await SaveEconomics(conn, "income_window_days", days);
            return null;
        }

        public const int DemolitionDays = 45;
        public const int MinNotSyncedMinutes = 10, MaxNotSyncedMinutes = 1440;
        public const int MaxLowSupply = 99999;
        public const int MaxGil = 999999999;
        public static readonly string[] PlayStyles = { "auto", "both", "subs", "retainers" };

        private static bool WholeUpTo(double n, double max) => n == Math.Floor(n) && n >= 0 && n <= max;

        /// <summary>Why the Warnings can't be saved (as the app's Settings → General → Warnings checks them), or null.</summary>
        public static string? CheckWarnings(double repairKits, double ceruleum, double houseDays, double notSyncedMinutes)
        {
            if (!WholeUpTo(repairKits, MaxLowSupply)) return $"Low repair kits must be a whole number from 0 to {MaxLowSupply} (0 turns it off).";
            if (!WholeUpTo(ceruleum, MaxLowSupply)) return $"Low ceruleum must be a whole number from 0 to {MaxLowSupply} (0 turns it off).";
            if (!(houseDays == Math.Floor(houseDays) && houseDays >= 1 && houseDays < DemolitionDays))
                return $"The house warning must be a whole number of days from 1 to {DemolitionDays - 1}.";
            if (!(notSyncedMinutes == Math.Floor(notSyncedMinutes) && notSyncedMinutes >= MinNotSyncedMinutes && notSyncedMinutes <= MaxNotSyncedMinutes))
                return $"Not synced must be a whole number of minutes from {MinNotSyncedMinutes} to {MaxNotSyncedMinutes}.";
            return null;
        }

        /// <summary>The Warnings: low repair kits and ceruleum (below these; 0: off), the FC house warning (days not
        /// entered) and when a character counts as not synced (minutes behind the rest of its account).</summary>
        public static async Task<string?> SaveWarnings(DbConnection conn, double repairKits, double ceruleum, double houseDays, double notSyncedMinutes)
        {
            if (CheckWarnings(repairKits, ceruleum, houseDays, notSyncedMinutes) is { } problem) return problem;
            await SaveEconomics(conn, "low_repair_kit_threshold", (decimal)repairKits);
            await SaveEconomics(conn, "low_ceruleum_threshold", (decimal)ceruleum);
            await SaveEconomics(conn, "house_warn_days", (decimal)houseDays);
            await SaveEconomics(conn, "not_synced_after_minutes", (decimal)notSyncedMinutes);
            return null;
        }

        /// <summary>What one repair kit costs, in whole gil (used for the routes' profit).</summary>
        public static async Task<string?> SaveRepairKitCost(DbConnection conn, double gil)
        {
            if (!WholeUpTo(gil, MaxGil)) return "The repair kit cost must be a whole number of gil, 0 or more.";
            await SaveEconomics(conn, "repair_kit_cost", (decimal)gil);
            return null;
        }

        private static async Task SaveSetting(DbConnection conn, string key, string value)
        {
            await using var cmd = Command(conn, @"INSERT INTO tier3_settings (setting_key, setting_value, updated_at) VALUES ($1, $2, now())
                ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value, updated_at = now()", key, value);
            await cmd.ExecuteNonQueryAsync();
        }

        /// <summary>Live Market Prices: whether to ask Universalis for prices.</summary>
        public static Task SaveLivePrices(DbConnection conn, bool on) => SaveSetting(conn, "universalis_enabled", on ? "true" : "false");

        /// <summary>"What do you run?": auto (work it out from the characters), both, subs or retainers.</summary>
        public static async Task<string?> SavePlayStyle(DbConnection conn, string choice)
        {
            if (!PlayStyles.Contains(choice)) return "Unknown choice.";
            await SaveSetting(conn, "play_style", choice);
            return null;
        }

        /// <summary>A PC no longer used: its row goes (it comes back if the plugin still runs there). How many went, or why not.</summary>
        public static async Task<(int Removed, string? Error)> RemovePc(DbConnection conn, string pcName, string? accountLabel)
        {
            if (string.IsNullOrEmpty(pcName)) return (0, "No PC given.");
            await using var cmd = Command(conn, "DELETE FROM companion_pc_status WHERE pc_name = $1 AND account_label = $2", pcName, accountLabel ?? "");
            return (await cmd.ExecuteNonQueryAsync(), null);
        }

        /// <summary>The saved ticks after ticking `made` of `crafts` for one part (setProgress): whole parts, at most crafts;
        /// none takes its tick off. Remembers the Look ahead it was ticked with (shopping-progress.js).</summary>
        public static string SetProgress(string? stored, int itemId, double made, double crafts, int ahead)
        {
            var entries = ShoppingMath.ParseProgress(stored).Select(e => (e.Key, Json: e.Entry.GetRawText())).ToList();
            var key = itemId.ToString(CultureInfo.InvariantCulture);
            var outOf = double.IsNaN(crafts) ? 0 : crafts;
            var n = Math.Max(0, Math.Min(outOf, Math.Floor((double.IsNaN(made) ? 0 : made) + 0.5)));
            var at = entries.FindIndex(e => e.Key == key);
            static string Num(double v) => v.ToString("R", CultureInfo.InvariantCulture);
            if (n > 0)
            {
                var json = ahead != 0 ? $"{{\"made\":{Num(n)},\"outOf\":{Num(outOf)},\"ahead\":{ahead}}}" : $"{{\"made\":{Num(n)},\"outOf\":{Num(outOf)}}}";
                if (at >= 0) entries[at] = (key, json); else entries.Add((key, json));
            }
            else if (at >= 0) entries.RemoveAt(at);
            // As JavaScript writes an object: whole-number keys smallest first, then the rest in the order they came.
            static bool IsIndex(string k) => k.Length > 0 && k.Length <= 10 && (k == "0" || k[0] != '0') && k.All(char.IsAsciiDigit) && ulong.Parse(k, CultureInfo.InvariantCulture) < uint.MaxValue;
            entries = entries.Where(e => IsIndex(e.Key)).OrderBy(e => ulong.Parse(e.Key, CultureInfo.InvariantCulture)).Concat(entries.Where(e => !IsIndex(e.Key))).ToList();
            var text = new StringBuilder("{");
            for (var i = 0; i < entries.Count; i++)
                text.Append(i > 0 ? "," : "").Append(JsonSerializer.Serialize(entries[i].Key)).Append(':').Append(entries[i].Json);
            return text.Append('}').ToString();
        }

        /// <summary>How many of a part on the Shopping List (looking `aheadDays` ahead) have been made, saved for every PC.</summary>
        public static async Task<string?> SaveShoppingProgress(DbConnection conn, double itemId, double made, double crafts, int aheadDays)
        {
            if (itemId != Math.Floor(itemId) || itemId <= 0 || itemId > int.MaxValue || crafts != Math.Floor(crafts) || crafts < 0 || double.IsNaN(made) || double.IsInfinity(made))
                return "Not a part on the list.";
            await using var tx = await conn.BeginTransactionAsync();
            string? stored = null;
            await using (var read = Command(conn, "SELECT setting_value FROM tier3_settings WHERE setting_key = $1", "shopping_progress"))
            {
                read.Transaction = tx;
                stored = await read.ExecuteScalarAsync() as string;
            }
            await using (var write = Command(conn, @"INSERT INTO tier3_settings (setting_key, setting_value, updated_at) VALUES ($1, $2, now())
                ON CONFLICT (setting_key) DO UPDATE SET setting_value = EXCLUDED.setting_value, updated_at = now()",
                "shopping_progress", SetProgress(stored, (int)itemId, made, crafts, ShoppingMath.LookAheadDays(aheadDays))))
            {
                write.Transaction = tx;
                await write.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
            return null;
        }
    }
}
