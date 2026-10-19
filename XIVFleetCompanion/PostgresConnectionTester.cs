using System;
using System.Threading.Tasks;
using Npgsql;

namespace XIVFleetCompanion
{
    public static class PostgresConnectionTester
    {
        /// <summary>
        /// Attempts to open a real connection to Postgres using the saved credential.
        /// Returns a human-readable result string — never throws.
        /// </summary>
        public static async Task<string> TestConnectionAsync(bool useRemote)
        {
            var cred = PostgresCredentialStore.Load(useRemote);
            var (connectionString, error) = PostgresWriter.BuildConnectionString(useRemote);
            if (cred == null || connectionString == null)
                return error ?? "Not configured — no saved credential found.";

            try
            {
                await using var conn = new NpgsqlConnection(connectionString);
                await conn.OpenAsync();
                return $"Success — connected to '{cred.Database}' at {cred.Host}:{cred.Port}.";
            }
            catch (Exception ex)
            {
                return $"Failed — {ex.Message}";
            }
        }
    }
}
