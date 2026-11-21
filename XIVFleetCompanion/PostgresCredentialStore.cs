using System;
using System.Text.Json;
using Meziantou.Framework.Win32;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Stores and retrieves Postgres connection details using Windows Credential Manager.
    /// The password is stored as the credential's secret; host/port/database ride along
    /// as a JSON blob in the credential's comment field.
    /// </summary>
    public static class PostgresCredentialStore
    {
        private const string LocalTargetName = "XIVFleetCompanion:Postgres:Local";
        private const string RemoteTargetName = "XIVFleetCompanion:Postgres:Remote";

        private static string GetTargetName(bool useRemote) => useRemote ? RemoteTargetName : LocalTargetName;

        private class ConnectionDetails
        {
            public string Host { get; set; } = string.Empty;
            public int Port { get; set; }
            public string Database { get; set; } = string.Empty;
        }

        public class PostgresCredential
        {
            public string Host { get; set; } = string.Empty;
            public int Port { get; set; }
            public string Database { get; set; } = string.Empty;
            public string Username { get; set; } = string.Empty;
            public string Password { get; set; } = string.Empty;
        }

        public static void Save(bool useRemote, string host, int port, string database, string username, string password)
        {
            var details = new ConnectionDetails { Host = host, Port = port, Database = database };
            var commentJson = JsonSerializer.Serialize(details);

            CredentialManager.WriteCredential(
                applicationName: GetTargetName(useRemote),
                userName: username,
                secret: password,
                comment: commentJson,
                persistence: CredentialPersistence.LocalMachine);
        }

        public static PostgresCredential? Load(bool useRemote)
        {
            try
            {
                var cred = CredentialManager.ReadCredential(GetTargetName(useRemote));
                if (cred == null)
                    return null;

                var details = string.IsNullOrEmpty(cred.Comment)
                    ? new ConnectionDetails()
                    : JsonSerializer.Deserialize<ConnectionDetails>(cred.Comment) ?? new ConnectionDetails();

                LoadProblem = null;
                return new PostgresCredential
                {
                    Host = details.Host,
                    Port = details.Port,
                    Database = details.Database,
                    Username = cred.UserName ?? string.Empty,
                    Password = cred.Password ?? string.Empty
                };
            }
            catch (Exception ex)
            {
                // Corrupted or inaccessible credential: the caller treats it as "not configured". Read for every sync and
                // read, so logged once until it changes; Settings says to enter the details again (LoadProblem).
                if (LoadProblem != ex.Message)
                    Plugin.Log.Warning($"Fleet Companion: could not read the saved database credentials — {ex.Message}");
                LoadProblem = ex.Message;
                return null;
            }
        }

        /// <summary>Why the saved database details couldn't be read last time (null when they could, or none are saved).</summary>
        public static string? LoadProblem { get; private set; }

        public static void Delete(bool useRemote)
        {
            try
            {
                CredentialManager.DeleteCredential(GetTargetName(useRemote));
            }
            catch
            {
                // Nothing to delete — fine.
            }
        }
    }
}
