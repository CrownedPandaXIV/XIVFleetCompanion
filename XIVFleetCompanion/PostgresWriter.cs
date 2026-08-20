using Npgsql;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Collections.Generic;

namespace XIVFleetCompanion
{
    public static class PostgresWriter
    {
        private static (string? connectionString, string? error) BuildConnectionString(bool useRemote)
        {
            var cred = PostgresCredentialStore.Load(useRemote);
            if (cred == null)
                return (null, "Not configured — no saved credential found.");

            var connectionString =
                $"Host={cred.Host};Port={cred.Port};Database={cred.Database};" +
                $"Username={cred.Username};Password={cred.Password};Timeout=5";

            return (connectionString, null);
        }
        public static async Task<string> WriteCharacterSnapshotAsync(
            ulong cid, string name, string world,
            int retainerCount, int submarineCount,
            uint gil, int ceruleum, int repairKits, bool useRemote)
        {
            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();

                const string sql = @"
                    INSERT INTO companion_character_snapshot
                        (cid, name, world, retainer_count, submarine_count, gil, ceruleum, repair_kits)
                    VALUES
                        (@cid, @name, @world, @retainer_count, @submarine_count, @gil, @ceruleum, @repair_kits)";

                await using var cmd = new NpgsqlCommand(sql, conn);
                cmd.Parameters.AddWithValue("cid", (decimal)cid);
                cmd.Parameters.AddWithValue("name", name);
                cmd.Parameters.AddWithValue("world", world);
                cmd.Parameters.AddWithValue("retainer_count", retainerCount);
                cmd.Parameters.AddWithValue("submarine_count", submarineCount);
                cmd.Parameters.AddWithValue("gil", (long)gil);
                cmd.Parameters.AddWithValue("ceruleum", ceruleum);
                cmd.Parameters.AddWithValue("repair_kits", repairKits);

                await cmd.ExecuteNonQueryAsync();

                return "Success.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }

        public static async Task<string> WriteInventorySnapshotAsync(
            ulong ownerCid, List<AllaganToolsConnector.ParsedItem> items, bool useRemote)
        {
            var (connectionString, connError) = BuildConnectionString(useRemote);
            if (connectionString == null)
                return connError!;

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                await using var transaction = await conn.BeginTransactionAsync();

                const string deleteSql = "DELETE FROM companion_inventory_snapshot WHERE owner_cid = @owner_cid";
                await using (var deleteCmd = new NpgsqlCommand(deleteSql, conn, transaction))
                {
                    deleteCmd.Parameters.AddWithValue("owner_cid", (decimal)ownerCid);
                    await deleteCmd.ExecuteNonQueryAsync();
                }

                const string insertSql = @"
                    INSERT INTO companion_inventory_snapshot
                        (owner_cid, retainer_id, sorted_container, sorted_slot_index, item_id, quantity)
                    VALUES
                        (@owner_cid, @retainer_id, @sorted_container, @sorted_slot_index, @item_id, @quantity)";

                foreach (var item in items)
                {
                    await using var insertCmd = new NpgsqlCommand(insertSql, conn, transaction);
                    insertCmd.Parameters.AddWithValue("owner_cid", (decimal)ownerCid);
                    insertCmd.Parameters.AddWithValue("retainer_id", (decimal)item.RetainerId);
                    insertCmd.Parameters.AddWithValue("sorted_container", (int)item.SortedContainer);
                    insertCmd.Parameters.AddWithValue("sorted_slot_index", item.SortedSlotIndex);
                    insertCmd.Parameters.AddWithValue("item_id", (int)item.ItemId);
                    insertCmd.Parameters.AddWithValue("quantity", (int)item.Quantity);
                    await insertCmd.ExecuteNonQueryAsync();
                }

                await transaction.CommitAsync();

                return $"Success — wrote {items.Count} items.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }
    }
}
