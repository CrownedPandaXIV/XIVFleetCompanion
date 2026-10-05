using Dalamud.Configuration;
using System;
using System.Collections.Generic;

namespace XIVFleetCompanion;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    // Fleet Companion settings
    public bool Enabled { get; set; } = false;
    public int SyncIntervalMinutes { get; set; } = 5;
    // Also sync a few seconds after a character logs out (AutoRetainer finished with it).
    public bool SyncAfterLogout { get; set; } = true;
    public DateTime? LastSyncTimestamp { get; set; } = null;
    public bool UseRemoteConnection { get; set; } = false;
    public string FCTrackerConfigPath { get; set; } = "";
    public string AccountLabel { get; set; } = "";

    // Which characters are synced (see CharacterChoice): every one except SkippedCharacters, or with
    // SyncOnlyChosen only ChosenCharacters.
    public bool SyncOnlyChosen { get; set; } = false;
    public HashSet<ulong> ChosenCharacters { get; set; } = new();
    public HashSet<ulong> SkippedCharacters { get; set; } = new();

    public bool ShouldSync(ulong cid) => CharacterChoice.ShouldSync(cid, SyncOnlyChosen, ChosenCharacters, SkippedCharacters);

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
