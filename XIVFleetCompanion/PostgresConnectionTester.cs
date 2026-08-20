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
        public static async Task<string> TestConnectionAsync()
        {
            var cred = PostgresCredentialStore.Load();
            if (cred == null)
                return "Not configured — no saved credential found.";

            var connectionString =
                $"Host={cred.Host};Port={cred.Port};Database={cred.Database};" +
                $"Username={cred.Username};Password={cred.Password};Timeout=5";

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
