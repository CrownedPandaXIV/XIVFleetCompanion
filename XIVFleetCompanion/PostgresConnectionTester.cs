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

            // The same way syncing connects (following a database that moved to another PC), and a failure in plain words.
            try
            {
                await using var conn = await PostgresWriter.OpenConnectionAsync(useRemote);
                if (conn == null) return "Not configured — no saved credential found.";
                var now = PostgresCredentialStore.Load(useRemote) ?? cred;
                return $"Success — connected to '{now.Database}' at {now.Host}:{now.Port}.";
            }
            catch (Exception ex)
            {
                return $"Failed — {SyncFailure.Describe(ex)}";
            }
        }
    }
}
