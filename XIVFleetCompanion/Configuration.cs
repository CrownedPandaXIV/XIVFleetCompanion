using Dalamud.Configuration;
using System;

namespace XIVFleetCompanion;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public bool IsConfigWindowMovable { get; set; } = true;

    // Fleet Companion settings
    public bool Enabled { get; set; } = false;
    public int SyncIntervalMinutes { get; set; } = 5;
    public DateTime? LastSyncTimestamp { get; set; } = null;
    public bool UseRemoteConnection { get; set; } = false;
    public string FCTrackerConfigPath { get; set; } = "";
    public string AccountLabel { get; set; } = "";

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
