using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Settings → Connections in use, as the app's (db/connections-in-use.js): how many of the shared database's connection
    /// slots are taken, by the app, the plugins and anything else, and from which PC. When they run out, the plugins can't
    /// sync at all. The app and the plugins name their connections, so the database's own list (pg_stat_activity) says
    /// whose each one is; its address is only shown for connections made with the same login (every PC set up by the app
    /// shares one), others (pgAdmin signed in as postgres, say) count as another login. No Dalamud here, so it is tested
    /// on its own.
    /// </summary>
    public static class ConnectionsInUse
    {
        public const string AppName = "XIV Fleet Companion app";
        /// <summary>The share of the slots in use from which the count is shown as a warning.</summary>
        public const double WarnShare = 0.8;

        // Client connections only (not the database's own workers): for another login's sessions the database hides the
        // kind, so those count when connected to a database. The one asking (this check) is left out and counted itself.
        public const string ListSql = @"SELECT application_name, host(client_addr), usename = current_user
            FROM pg_stat_activity
            WHERE (backend_type = 'client backend' OR (backend_type IS NULL AND datid IS NOT NULL)) AND pid <> pg_backend_pid()";

        // The slots an ordinary login may use: max_connections less those kept for superusers (and, PostgreSQL 16 and
        // later, for roles given pg_use_reserved_connections); and this connection's own address.
        public const string LimitSql = @"SELECT current_setting('max_connections')::int, current_setting('superuser_reserved_connections')::int,
            COALESCE(NULLIF(current_setting('reserved_connections', true), ''), '0')::int, host(inet_client_addr())";

        public sealed record Row(string? Name, string? Address, bool Mine);
        public sealed record Limits(int Max, int Superuser, int Reserved, string? Me);

        public sealed class Pc
        {
            public string Key = "";
            public string Label = "";
            public int App, Plugins, Other;
            public int Total => App + Plugins + Other;
        }

        public sealed record Summary(int InUse, int Limit, bool Warn, int App, int Plugins, int Other, List<Pc> Pcs);

        public static string KindOf(string? name) =>
            name == AppName ? "app" : name == DatabaseMove.ApplicationName ? "plugins" : "other";

        private static bool Loopback(string? addr) =>
            addr is "127.0.0.1" or "::1" || (addr != null && Regex.IsMatch(addr, @"^::ffff:127\."));

        /// <summary>Where a connection comes from. One through the database PC's own socket has no address.</summary>
        public static (string Key, string Label) PlaceOf(Row row, string? me)
        {
            if (!row.Mine) return ("hidden", "Another login (its PC isn't shown)");
            var local = string.IsNullOrEmpty(row.Address) || Loopback(row.Address);
            var meLocal = string.IsNullOrEmpty(me) || Loopback(me);
            if (local && meLocal) return ("this", "This PC (the database's PC)");
            if (local) return ("database", "The database's PC");
            if (row.Address == me) return ("this", "This PC");
            return (row.Address!, row.Address!);
        }

        private static int Order(string key) => key switch { "this" => 0, "database" => 1, "hidden" => 3, _ => 2 };

        /// <summary>The count, the asking connection counted in (it is one of the slots, if only for a moment), as the plugin's.</summary>
        public static Summary Summarize(IEnumerable<Row> rows, Limits limits)
        {
            var limit = Math.Max(1, limits.Max - limits.Superuser - limits.Reserved);
            var pcs = new Dictionary<string, Pc>();
            int app = 0, plugins = 0, other = 0;
            void Add(Row row, string kind)
            {
                var (key, label) = PlaceOf(row, limits.Me);
                if (!pcs.TryGetValue(key, out var pc)) pcs[key] = pc = new Pc { Key = key, Label = label };
                switch (kind)
                {
                    case "app": pc.App++; app++; break;
                    case "plugins": pc.Plugins++; plugins++; break;
                    default: pc.Other++; other++; break;
                }
            }
            Add(new Row(DatabaseMove.ApplicationName, limits.Me, true), "plugins");
            foreach (var row in rows) Add(row, KindOf(row.Name));
            var inUse = app + plugins + other;
            var list = pcs.Values.OrderBy(p => Order(p.Key)).ThenByDescending(p => p.Total).ThenBy(p => p.Label, StringComparer.OrdinalIgnoreCase).ToList();
            return new Summary(inUse, limit, inUse >= WarnShare * limit, app, plugins, other, list);
        }

        /// <summary>Reads the count from the shared database (PostgreSQL; null for anything else).</summary>
        public static async Task<Summary?> Read(DbConnection conn)
        {
            if (conn.GetType().Name != "NpgsqlConnection") return null;
            var rows = new List<Row>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = ListSql;
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    rows.Add(new Row(r.IsDBNull(0) ? null : r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), !r.IsDBNull(2) && r.GetBoolean(2)));
            }
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = LimitSql;
                await using var r = await cmd.ExecuteReaderAsync();
                await r.ReadAsync();
                return Summarize(rows, new Limits(r.GetInt32(0), r.GetInt32(1), r.GetInt32(2), r.IsDBNull(3) ? null : r.GetString(3)));
            }
        }
    }
}
