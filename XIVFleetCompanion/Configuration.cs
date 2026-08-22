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

    // Retention/downsampling settings for companion_character_snapshot cleanup.
    // RetentionValue/Unit: how old a row must be before it becomes eligible for compression.
    // DownsampleValue/Unit: how coarse compressed data becomes (e.g. 1 Days = keep one row per day).
    public int RetentionValue { get; set; } = 6;
    public string RetentionUnit { get; set; } = "Months";
    public int DownsampleValue { get; set; } = 1;
    public string DownsampleUnit { get; set; } = "Days";

    // The below exists just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
