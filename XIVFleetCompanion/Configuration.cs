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
    // 2.0: "Fleet" in the game's info bar (top right), with what needs attention; click opens the Summary.
    public bool ShowInInfoBar { get; set; } = true;
    // 2.x: the look of the plugin's window (Theme.cs) and colour-blind friendly status colors.
    public ThemeChoice Theme { get; set; } = ThemeChoice.Fleet;
    public bool ColourBlindStatus { get; set; } = false;
    // The main window's sidebar folded down to its icons.
    public bool SidebarFolded { get; set; } = false;
    // 2.x tables: each table's shown columns in order (the Columns button; none saved: the table's own), and whether
    // it groups by account when not sorted by a column (none saved: yes, as the app).
    public Dictionary<string, List<string>> TableColumns { get; set; } = new();
    public Dictionary<string, bool> TableGroupByAccount { get; set; } = new();
    // The Inventory page's "Hide equipped gear" (Characters, Fleet totals and Where is it?): leaves out gear being worn
    // by the characters and their retainers.
    public bool HideEquippedGear { get; set; } = false;
    // The Parts page's Shopping List: buy from vendors what they sell (instead of crafting or gathering it), show items
    // already held in full, and where market board prices are from ("dc:Dynamis"; none saved: the data center with the
    // most characters).
    // The Parts page's Look ahead (0, 3, 7 or 14 days), kept as the app keeps its own.
    public int PartsAheadDays { get; set; } = 0;
    public bool ShoppingBuyFromVendors { get; set; } = false;
    public bool ShoppingShowHeld { get; set; } = false;
    public string? ShoppingPriceScope { get; set; } = null;
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
