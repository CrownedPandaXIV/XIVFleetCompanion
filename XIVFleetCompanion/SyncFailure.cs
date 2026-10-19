using Npgsql;
using System;
using System.Net.Sockets;

namespace XIVFleetCompanion
{
    /// <summary>
    /// Why a sync didn't get through, in plain words for the main window and the log. A problem the player can
    /// fix (the database PC is off, a wrong password) is logged once, until syncing works again; anything else
    /// is logged in full every time, as it may be a bug. No Dalamud here, so it is tested on its own
    /// (tests/FleetWriterTests).
    /// </summary>
    public static class SyncFailure
    {
        public const string CantReach = "Can't reach the database. Check the PC with the database is on, and that Tailscale is connected if you use it. It tries again at every sync.";
        public const string WrongLogin = "The database refused the username or password. Check them in the plugin's settings (they're the same as in the app).";
        public const string NoDatabase = "The database named in the plugin's settings doesn't exist there. Check the name matches the app's.";
        public const string NoCredential = "No database connection saved yet. Open the plugin's settings and enter the same details as in the app.";

        /// <summary>What to show for this failure.</summary>
        public static string Describe(Exception ex) => ex switch
        {
            DatabaseMove.WaitException or DatabaseMove.UnreachableException => ex.Message,
            PostgresException pg when pg.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification => WrongLogin,
            PostgresException pg when pg.SqlState == PostgresErrorCodes.InvalidCatalogName => NoDatabase,
            PostgresException => ex.Message,
            NpgsqlException or TimeoutException or SocketException => CantReach,
            _ => ex.Message,
        };

        /// <summary>Whether it's a problem the player can fix, rather than something that may be a bug.</summary>
        public static bool IsExpected(Exception ex) => ex switch
        {
            DatabaseMove.WaitException or DatabaseMove.UnreachableException => true,
            PostgresException pg => pg.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification
                or PostgresErrorCodes.InvalidCatalogName,
            NpgsqlException or TimeoutException or SocketException => true,
            _ => false,
        };
    }
}
