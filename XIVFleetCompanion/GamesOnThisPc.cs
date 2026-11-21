using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace XIVFleetCompanion
{
    /// <summary>
    /// The games on this PC that sync, noted in games.json beside the fleet file: each game's plugin settings folder,
    /// its Account Label and when it last synced. A sync's setup is this PC's name and the Account Label, so two games
    /// on one PC with the same label (blank included) would write the same PCs-page row and the same logins, each
    /// overwriting the other's. Then each game says to give it its own label. No Dalamud here, so it is tested on its own.
    /// </summary>
    public static class GamesOnThisPc
    {
        public const string FileName = "games.json";

        /// <summary>How long a game that stopped syncing still counts.</summary>
        public static readonly TimeSpan Recent = TimeSpan.FromDays(2);

        public sealed record Game(string Label, DateTime LastSync);

        /// <summary>Notes this game's sync and says whether another recent game on this PC has the same label. Never throws
        /// (a file another game is writing right then is simply read again next time).</summary>
        public static bool NoteAndCheck(string file, string configFolder, string? label, DateTime now)
        {
            var games = Read(file);
            var mine = (label ?? "").Trim();
            games[configFolder] = new Game(mine, now);
            foreach (var old in games.Where(g => now - g.Value.LastSync > Recent * 10).Select(g => g.Key).ToList()) games.Remove(old);
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                var temp = $"{file}.{Environment.ProcessId}.tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(games));
                File.Move(temp, file, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Next sync.
            }
            return games.Any(g => g.Key != configFolder && now - g.Value.LastSync <= Recent
                                  && string.Equals(g.Value.Label, mine, StringComparison.OrdinalIgnoreCase));
        }

        private static Dictionary<string, Game> Read(string file)
        {
            try
            {
                return File.Exists(file) ? JsonSerializer.Deserialize<Dictionary<string, Game>>(File.ReadAllText(file)) ?? new() : new();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return new();
            }
        }
    }
}
