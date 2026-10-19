using Npgsql;
using System;
using System.Threading.Tasks;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The app's "Move the database to this PC" (app 0.35.0, database update 10) leaves a note in the database:
    /// "pausing" while it copies everything (the plugin waits, so nothing is written mid-copy), then "moved" in
    /// the old database with the new PC's address, which the plugin switches to by itself. Read every time a
    /// connection is opened. No Dalamud here, so it is tested on its own (tests/FleetWriterTests).
    /// </summary>
    public static class DatabaseMove
    {
        /// <summary>Set on every plugin connection, so the app can tell when a sync is still running.</summary>
        public const string ApplicationName = "XIV Fleet Companion plugin";

        public enum Next { Sync, Wait, Follow }

        public sealed class Note
        {
            public string State = "";
            public string Host = "";
            public int Port;
            // Whether the app moving it said it was still working in the last 5 minutes (by the database's clock).
            public bool Fresh;
        }

        /// <summary>The note, or null when there is none (or the database doesn't have update 10 yet).</summary>
        public static async Task<Note?> ReadAsync(NpgsqlConnection conn)
        {
            try
            {
                await using var cmd = new NpgsqlCommand(
                    "SELECT state, host, port, updated_at > now() - interval '5 minutes' FROM companion_database_move WHERE id = 1", conn);
                await using var reader = await cmd.ExecuteReaderAsync();
                if (!await reader.ReadAsync()) return null;
                return new Note
                {
                    State = reader.GetString(0),
                    Host = reader.GetString(1),
                    Port = reader.GetInt32(2),
                    Fresh = reader.GetBoolean(3),
                };
            }
            catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                return null;
            }
        }

        /// <summary>
        /// What to do with this database: sync to it, wait (a move is copying it), or follow it to its new
        /// address. A "pausing" note the app stopped refreshing (it was closed mid-move) is ignored.
        /// </summary>
        public static Next Decide(Note? note)
        {
            if (note == null) return Next.Sync;
            if (note.State == "pausing" && note.Fresh) return Next.Wait;
            if (note.State == "moved" && !string.IsNullOrWhiteSpace(note.Host) && note.Port > 0) return Next.Follow;
            return Next.Sync;
        }

        /// <summary>A move is copying the database: try again in a minute.</summary>
        public sealed class WaitException : InvalidOperationException
        {
            public WaitException() : base("The database is being moved to another PC, so syncing waits until that's done.") { }
        }

        /// <summary>The database moved, but this PC can't reach its new address yet.</summary>
        public sealed class UnreachableException : InvalidOperationException
        {
            public string Where { get; }

            public UnreachableException(string where, string reason)
                : base($"The database moved to {where}, but this PC can't reach it yet ({reason}). Nothing is synced until it can; it tries again at every sync.")
            {
                Where = where;
            }
        }
    }
}
