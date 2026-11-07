using Npgsql;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    public static class PostgresWriter
    {
        // Built with NpgsqlConnectionStringBuilder so a password (or any other value)
        // containing ';' or '=' cannot break or change the connection settings.
        internal static (string? connectionString, string? error) BuildConnectionString(bool useRemote)
        {
            var cred = PostgresCredentialStore.Load(useRemote);
            if (cred == null)
                return (null, "Not configured — no saved credential found.");
            return (BuildConnectionString(cred), null);
        }

        private static string BuildConnectionString(PostgresCredentialStore.PostgresCredential cred)
        {
            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = cred.Host,
                Port = cred.Port,
                Database = cred.Database,
                Username = cred.Username,
                Password = cred.Password,
                Timeout = 5,
                IncludeErrorDetail = true,
                ApplicationName = DatabaseMove.ApplicationName,
            };
            return builder.ConnectionString;
        }

        private static async Task<NpgsqlConnection> OpenAsync(PostgresCredentialStore.PostgresCredential cred)
        {
            var conn = new NpgsqlConnection(BuildConnectionString(cred));
            try
            {
                await conn.OpenAsync();
            }
            catch
            {
                await conn.DisposeAsync();
                throw;
            }
            return conn;
        }

        // An open connection for one sync, or null when no credential is saved. Connection
        // errors are thrown to the caller, which logs them and tries again next sync.
        // When the app has moved the database to another PC (DatabaseMove), this PC follows it: the new
        // address is tried with the same database, login and password, and saved once it answers. While a
        // move is copying the database, DatabaseMove.WaitException is thrown instead, and
        // DatabaseMove.UnreachableException when the new address doesn't answer from this PC.
        public static async Task<NpgsqlConnection?> OpenConnectionAsync(bool useRemote)
        {
            var cred = PostgresCredentialStore.Load(useRemote);
            if (cred == null)
                return null;

            PostgresCredentialStore.PostgresCredential current = cred;
            var conn = await OpenAsync(current);
            return await DatabaseMove.FollowAsync(conn,
                (host, port) => OpenAsync(new PostgresCredentialStore.PostgresCredential
                {
                    Host = host, Port = port, Database = current.Database, Username = current.Username, Password = current.Password,
                }),
                (host, port) =>
                {
                    PostgresCredentialStore.Save(useRemote, host, port, current.Database, current.Username, current.Password);
                    Plugin.Log.Information($"Fleet Companion: the database moved from {current.Host}:{current.Port} to {host}:{port}. This PC now uses it there (saved in the plugin's settings).");
                    current = new PostgresCredentialStore.PostgresCredential { Host = host, Port = port, Database = current.Database, Username = current.Username, Password = current.Password };
                });
        }
    }
}
