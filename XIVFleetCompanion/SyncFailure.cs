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
        public const string WrongLogin = "The database refused the username or password. Check them in the plugin's Settings (they're the same as on your other PCs).";
        public const string NoDatabase = "The database named in the plugin's Settings doesn't exist there. Check the name matches the one your other PCs use.";
        public const string NoCredential = "No shared database saved yet. Enter its details in the plugin's Settings (the same as on your other PCs), or make one with Settings → Database tools.";
        public const string NotChosen = "Choose where your fleet data lives first (the plugin's window asks).";
        public const string FileBusy = "Another game on this PC kept the fleet file busy for too long. It tries again at the next sync.";
        public const string FileDiskFull = "The disk with the fleet file is full, so nothing could be saved. Free some space; it tries again at the next sync.";
        public const string FileReadOnly = "The fleet file can't be written to: it's read-only, or Windows or antivirus software is blocking it. It tries again at the next sync.";
        public const string FileCantOpen = "The fleet file couldn't be opened: its folder may be missing or blocked. It tries again at the next sync.";
        public const string Starting = "The database is still starting up (its PC was just turned on or restarted). It tries again at the next sync.";
        public const string TooBusy = "The database has too many connections open right now. It tries again at the next sync; if it keeps happening, restart the database PC.";
        public const string NotAllowed = "The database login in the plugin's Settings isn't allowed to change the fleet's tables. If the database was moved or made again, use the login your other PCs use now.";
        public const string ServerFull = "The disk on the database PC is full, so nothing could be saved. Free some space there; it tries again at the next sync.";
        public const string FileDamaged = "The fleet file is damaged. Restore a backup on the Status page (Restore a backup).";

        /// <summary>What to show for this failure.</summary>
        public static string Describe(Exception ex) => ex switch
        {
            DatabaseMove.WaitException or DatabaseMove.UnreachableException or FileDatabase.RefusedException => ex.Message,
            System.Data.SQLite.SQLiteException lite when Plain(lite) is { } text => text,
            System.Data.SQLite.SQLiteException => $"Couldn't use the fleet file: {ex.Message}",
            PostgresException pg when pg.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification => WrongLogin,
            PostgresException pg when pg.SqlState == PostgresErrorCodes.InvalidCatalogName => NoDatabase,
            PostgresException pg when PlainPg(pg) is { } text => text,
            PostgresException => ex.Message,
            NpgsqlException or TimeoutException or SocketException => CantReach,
            _ => ex.Message,
        };

        /// <summary>Whether it's a problem the player can fix, rather than something that may be a bug.</summary>
        public static bool IsExpected(Exception ex) => ex switch
        {
            DatabaseMove.WaitException or DatabaseMove.UnreachableException or FileDatabase.RefusedException => true,
            System.Data.SQLite.SQLiteException lite => Plain(lite) != null,
            PostgresException pg => pg.SqlState is PostgresErrorCodes.InvalidPassword or PostgresErrorCodes.InvalidAuthorizationSpecification
                or PostgresErrorCodes.InvalidCatalogName || PlainPg(pg) != null,
            NpgsqlException or TimeoutException or SocketException => true,
            _ => false,
        };

        // The shared database's other problems a player can fix (or that pass), in plain words; null for anything else.
        private static string? PlainPg(PostgresException ex) => ex.SqlState switch
        {
            PostgresErrorCodes.CannotConnectNow => Starting,                    // 57P03: starting up or shutting down
            PostgresErrorCodes.TooManyConnections => TooBusy,                   // 53300
            PostgresErrorCodes.InsufficientPrivilege => NotAllowed,             // 42501
            PostgresErrorCodes.DiskFull or PostgresErrorCodes.InsufficientResources => ServerFull, // 53100, 53000
            _ => null,
        };

        // The fleet file's problems a player can fix, in plain words; null for anything else. SQLite's extended codes
        // (such as a read-only file that was moved) keep the main code in their low byte.
        private static string? Plain(System.Data.SQLite.SQLiteException ex) =>
            (System.Data.SQLite.SQLiteErrorCode)((int)ex.ResultCode & 0xFF) switch
            {
                System.Data.SQLite.SQLiteErrorCode.Busy or System.Data.SQLite.SQLiteErrorCode.Locked => FileBusy,
                System.Data.SQLite.SQLiteErrorCode.Full => FileDiskFull,
                System.Data.SQLite.SQLiteErrorCode.ReadOnly or System.Data.SQLite.SQLiteErrorCode.Perm => FileReadOnly,
                System.Data.SQLite.SQLiteErrorCode.CantOpen => FileCantOpen,
                System.Data.SQLite.SQLiteErrorCode.Corrupt or System.Data.SQLite.SQLiteErrorCode.NotADb => FileDamaged,
                _ => null,
            };
    }
}
