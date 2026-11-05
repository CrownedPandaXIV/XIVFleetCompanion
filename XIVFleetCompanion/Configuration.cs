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
    // The account label this setup's logins were last recorded under (null: none yet), so a changed label can
    // move them along (see Plugin.KeepOwnCharactersAsync).
    public string? LoginsRecordedAs { get; set; } = null;
    // Shown in the plugin's window and settings while AccountLabel is blank: the app tells accounts apart by it.
    public const string NoAccountLabelText =
        "Give this setup an Account Label (for example, the account's name) so the app can tell your accounts apart and spot two setups syncing the same character.";

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
