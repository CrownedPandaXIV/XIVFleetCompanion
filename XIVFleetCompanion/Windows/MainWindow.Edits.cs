using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;

namespace XIVFleetCompanion.Windows;

// 2.x "ticking things off" stage: the edits the app makes, from the game (FleetEdits, saved in the background by
// FleetReader.Edit, then everything read again): Craft? on the Subs page, Sub Rules on the Parts page, gear targets on
// the Retainers page, the Roster order, Gil & Market's unsold days, the Income window, removing a PC, and ticking parts
// off on the Shopping List. The app sees each change at its next refresh, and the plugin sees the app's.
public partial class MainWindow
{
    private static string Inv(long n) => n.ToString(CultureInfo.InvariantCulture);

    /// <summary>"Saving..." while an edit is saved, then for a few seconds whether it was (green) or why not (amber).</summary>
    private void EditLine()
    {
        if (reader.Editing)
        {
            Colored(Dim, "Saving...");
            return;
        }
        if (reader.LastEdit is not { } last || DateTime.Now - last.At > TimeSpan.FromSeconds(8)) return;
        if (last.Error == null) Colored(Green, $"Saved: {last.What}.");
        else
            using (ImRaii.PushColor(ImGuiCol.Text, Amber))
                Ui.Wrapped($"Couldn't save {last.What}: {last.Error}");
    }

    // ---- Craft? (Subs page) ----

    // Ticked or not as last saved here (until the next read shows it), else as read.
    private readonly Dictionary<string, (bool On, DateTime At)> craftPending = new();

    private bool CraftOf(long cid, string subName)
    {
        var key = $"{cid}|{subName}";
        if (craftPending.TryGetValue(key, out var p) && !(reader.Parts.At > p.At)) return p.On;
        return reader.Parts.Data?.Inputs?.Subs.FirstOrDefault(s => s.Cid == cid && s.SubName == subName)?.CraftEnabled ?? false;
    }

    private void CraftCell(long cid, string subName)
    {
        if (cid == 0 || reader.Parts.Data?.Inputs == null) { Colored(Dim, "—"); return; }
        var on = CraftOf(cid, subName);
        if (Ui.Checkbox($"##craft{cid}|{subName}", ref on))
        {
            craftPending[$"{cid}|{subName}"] = (on, DateTime.Now);
            reader.Edit($"Craft? for {subName}", async c => { await FleetEdits.SetCraft(c, Inv(cid), subName, on); return null; });
        }
        Hover("Ticked: from rank 85 this sub follows Fight Club's best build after repair kits (Parts, Route Optimization). Unticked: its Sub Rules.");
    }

    // ---- Sub Rules (Parts page) ----

    private sealed class RuleDraft
    {
        public long? Id;
        public int Rank;
        public string? Cid;
        public string Filter = "";
        public string Hull = "", Stern = "", Bow = "", Bridge = "";
        public bool ConfirmDelete;
        // The rule as read when this draft began: an unchanged draft follows the database (a change made in the app).
        public PartsRule? Read;
        public bool Changed => Read == null || Rank != Read.Rank || Cid != Read.CidOverride || Filter != (Read.SubFilter ?? "") || Hull != (Read.Hull ?? "")
            || Stern != (Read.Stern ?? "") || Bow != (Read.Bow ?? "") || Bridge != (Read.Bridge ?? "");
    }

    private readonly Dictionary<long, RuleDraft> ruleDrafts = new();
    private readonly List<RuleDraft> newRules = new();
    private static readonly string[] PartLetters = { "", "S", "U", "W", "C", "Y", "S+", "U+", "W+", "C+", "Y+" };

    // "Sub1,Sub3" -> slots 1 and 3 ("", "All": every slot; an old "Sub2Only": slot 2), as the app's parseSubFilterToSlots.
    private static (bool All, List<int> Slots) FilterSlots(string? filter)
    {
        var f = (filter ?? "").Trim();
        if (f.Length == 0 || f.Equals("all", StringComparison.OrdinalIgnoreCase)) return (true, new List<int>());
        var legacy = System.Text.RegularExpressions.Regex.Match(f, "^Sub(\\d)Only$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (legacy.Success) return (false, new List<int> { legacy.Groups[1].Value[0] - '0' });
        if (f.Contains(',') || System.Text.RegularExpressions.Regex.IsMatch(f, "^Sub\\d$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            return (false, f.Split(',').Select(s => System.Text.RegularExpressions.Regex.Match(s.Trim(), "^Sub(\\d)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                .Where(m => m.Success).Select(m => m.Groups[1].Value[0] - '0').ToList());
        return (true, new List<int>());
    }

    private static string SlotsFilter(bool all, IEnumerable<int> slots) =>
        all || !slots.Any() ? "" : string.Join(",", slots.OrderBy(n => n).Select(n => $"Sub{n}"));

    private RuleDraft DraftOf(PartsRule r)
    {
        if (ruleDrafts.TryGetValue(r.Id, out var d) && (d.Changed || d.ConfirmDelete)) return d;
        return ruleDrafts[r.Id] = new RuleDraft
        {
            Id = r.Id, Rank = r.Rank, Cid = r.CidOverride, Filter = r.SubFilter ?? "", Hull = r.Hull ?? "", Stern = r.Stern ?? "", Bow = r.Bow ?? "",
            Bridge = r.Bridge ?? "", Read = r,
        };
    }

    private void DrawSubRules(PartsRead parts)
    {
        if (parts.Inputs == null)
        {
            Ui.Wrapped("The Sub Rules need the app's 1.8.0 views. Update the app and open it once.");
            return;
        }
        EditLine();
        Colored(Dim, "The build each sub should have from a rank: for some slots and maybe one character. A sub ticked Craft? follows Fight Club's builds from rank 85 instead." + (WithApp ? " Changes are saved for the app too." : ""));
        var characters = parts.Inputs.Characters.OrderBy(c => c.SortIndex ?? int.MaxValue).ThenBy(c => c.Name, StringComparer.Ordinal).ToList();
        var rules = parts.Inputs.Rules.OrderBy(r => r.Rank).ThenBy(r => r.Id).Select(DraftOf).Concat(newRules).ToList();
        if (ImGui.Button("Add a rule##subRules")) newRules.Add(new RuleDraft());
        void SubRulesTable()
        {
            using var table = Ui.Table("##subRules", 9, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit);
            if (!table) return;
            foreach (var h in new[] { "Rank", "Applies to", "Hull", "Stern", "Bow", "Bridge", "Character", "", "" }) Ui.SetupColumn(h);
            Ui.HeadersRow();
            var scale = ImGuiHelpers.GlobalScale;
            foreach (var d in rules)
            {
                using var id = ImRaii.PushId(d.Id is long n ? $"rule{n}" : $"new{newRules.IndexOf(d)}");
                Ui.NextRow();
                Ui.NextColumn();
                ImGui.SetNextItemWidth(70 * scale);
                Ui.InputInt("##rank", ref d.Rank, 0);
                Ui.NextColumn();
                var (all, slots) = FilterSlots(d.Filter);
                if (Ui.Checkbox("All##slots", ref all) && all) d.Filter = "";
                for (var s = 1; s <= 4; s++)
                {
                    ImGui.SameLine();
                    var on = !all && slots.Contains(s);
                    if (Ui.Checkbox($"{s}##slot{s}", ref on))
                        d.Filter = SlotsFilter(false, on ? (all ? new List<int>() : slots).Append(s) : slots.Where(x => x != s));
                }
                foreach (var (label, get, set) in new (string, Func<string>, Action<string>)[]
                         {
                             ("hull", () => d.Hull, v => d.Hull = v), ("stern", () => d.Stern, v => d.Stern = v),
                             ("bow", () => d.Bow, v => d.Bow = v), ("bridge", () => d.Bridge, v => d.Bridge = v),
                         })
                {
                    Ui.NextColumn();
                    ImGui.SetNextItemWidth(60 * scale);
                    using var combo = Ui.Combo($"##{label}", get().Length > 0 ? get() : "—");
                    if (combo)
                        foreach (var letter in PartLetters)
                            if (Ui.Selectable(letter.Length > 0 ? letter : "—", get() == letter)) set(letter);
                }
                Ui.NextColumn();
                ImGui.SetNextItemWidth(200 * scale);
                var who = d.Cid == null ? "Everyone" : characters.FirstOrDefault(c => Inv(c.Cid) == d.Cid) is { } ch ? $"{ch.Name} ({ch.World})" : d.Cid;
                using (var combo = Ui.Combo("##character", who))
                {
                    if (combo)
                    {
                        if (Ui.Selectable("Everyone", d.Cid == null)) d.Cid = null;
                        foreach (var c in characters)
                            if (Ui.Selectable($"{c.Name} ({c.World})##{c.Cid}", d.Cid == Inv(c.Cid))) d.Cid = Inv(c.Cid);
                    }
                }
                Ui.NextColumn();
                using (ImRaii.Disabled(reader.Editing))
                {
                    if (ImGui.SmallButton("Save"))
                    {
                        var rule = new FleetEdits.Rule(d.Id, d.Rank, d.Cid, d.Filter, d.Hull, d.Stern, d.Bow, d.Bridge);
                        var draft = d;
                        reader.Edit($"the rank {d.Rank} rule", async c =>
                        {
                            if (rule.Id == null) await FleetEdits.AddRule(c, rule); else await FleetEdits.UpdateRule(c, rule);
                            return null;
                        }, error =>
                        {
                            if (error != null) return;
                            // Saved: shown as read from now on.
                            if (draft.Id is long saved) ruleDrafts.Remove(saved); else newRules.Remove(draft);
                        });
                    }
                }
                Ui.NextColumn();
                if (d.Id == null)
                {
                    if (ImGui.SmallButton("Cancel")) newRules.Remove(d);
                }
                else if (!d.ConfirmDelete)
                {
                    if (ImGui.SmallButton("Delete")) d.ConfirmDelete = true;
                }
                else
                {
                    using (ImRaii.PushColor(ImGuiCol.Button, Red with { W = 0.6f }))
                    {
                        if (ImGui.SmallButton("Delete it"))
                        {
                            var ruleId = d.Id.Value;
                            reader.Edit($"deleting the rank {d.Rank} rule", async c => { await FleetEdits.DeleteRule(c, ruleId); return null; },
                                error => { if (error == null) ruleDrafts.Remove(ruleId); });
                        }
                    }
                    ImGui.SameLine();
                    if (ImGui.SmallButton("Keep")) d.ConfirmDelete = false;
                }
            }
        }
        ImGui.SameLine();
        TableExportUi.Button("subRules", "Sub Rules", () => TableCapture.Run(SubRulesTable));
        SubRulesTable();
    }

    // ---- Gear targets (Retainers page) ----

    private (string Mode, int Value, bool OnlyCrafted) targetDraft;

    /// <summary>The gear target cell: the target, and Edit (a small window with the mode, value and crafted-only, Save and Clear).</summary>
    private void TargetCell(RetainerRow r)
    {
        if (r.TargetMode == null) Colored(Dim, "—");
        else Ui.Text($"{(r.TargetMode == "level" ? "Level" : "ilvl")} {r.TargetValue}{(r.OnlyCrafted ? ", crafted only" : "")}");
        ImGui.SameLine();
        var popupId = $"target##{r.RetainerId}";
        if (ImGui.SmallButton($"Edit##target{r.RetainerId}"))
        {
            targetDraft = (r.TargetMode ?? "", r.TargetValue ?? 0, r.OnlyCrafted);
            ImGui.OpenPopup(popupId);
        }
        using var popup = ImRaii.Popup(popupId);
        if (!popup) return;
        Ui.Text($"Gear target for {r.Name}");
        ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
        using (var combo = Ui.Combo("Target mode##targetMode", targetDraft.Mode == "level" ? "Level" : targetDraft.Mode == "ilvl" ? "ilvl" : "—"))
        {
            if (combo)
                foreach (var (mode, label) in new[] { ("", "—"), ("level", "Level"), ("ilvl", "ilvl") })
                    if (Ui.Selectable(label, targetDraft.Mode == mode)) targetDraft.Mode = mode;
        }
        using (ImRaii.Disabled(targetDraft.Mode.Length == 0))
        {
            ImGui.SetNextItemWidth(110 * ImGuiHelpers.GlobalScale);
            Ui.InputInt("Target value##targetValue", ref targetDraft.Value, 0);
        }
        Ui.Checkbox("Only crafted gear counts##targetCrafted", ref targetDraft.OnlyCrafted);
        using (ImRaii.Disabled(reader.Editing))
        {
            if (ImGui.Button("Save##target"))
            {
                var (mode, value, crafted) = targetDraft;
                var id = Inv(r.RetainerId);
                reader.Edit($"{r.Name}'s gear target", c => FleetEdits.SaveTarget(c, id, mode.Length > 0 ? mode : null, mode.Length > 0 || value != 0 ? value : null, crafted));
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Clear##target"))
            {
                var id = Inv(r.RetainerId);
                reader.Edit($"clearing {r.Name}'s gear target", c => FleetEdits.DeleteTarget(c, id));
                ImGui.CloseCurrentPopup();
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##target")) ImGui.CloseCurrentPopup();
    }

    // ---- The Roster order (Roster page) ----

    private List<RosterRow>? rosterOrderDraft;

    /// <summary>Change order: the characters in Roster order, each moved up, down, or to the top, then saved in one go.</summary>
    private void DrawRosterOrder()
    {
        var draft = rosterOrderDraft!;
        Colored(Dim, "Move characters up or down, then save. The order is the Roster's everywhere" + (WithApp ? ", in the game and the app." : "."));
        using (ImRaii.Disabled(reader.Editing))
        {
            if (ImGui.Button("Save order##rosterOrder"))
            {
                var cids = draft.Select(c => Inv(c.Cid)).ToList();
                reader.Edit("the Roster order", async c => { await FleetEdits.SaveOrder(c, cids); return null; }, error => { if (error == null) rosterOrderDraft = null; });
            }
        }
        ImGui.SameLine();
        if (ImGui.Button("Cancel##rosterOrder")) rosterOrderDraft = null;
        using var table = Ui.Table("##rosterOrder", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.ScrollY,
            new Vector2(0, Math.Max(160 * ImGuiHelpers.GlobalScale, ImGui.GetContentRegionAvail().Y)));
        if (!table) return;
        Ui.ScrollFreeze(0, 1);
        Ui.SetupColumn("#");
        Ui.SetupColumn("Character");
        Ui.SetupColumn("Move");
        Ui.HeadersRow();
        for (var i = 0; i < draft.Count; i++)
        {
            var c = draft[i];
            using var id = ImRaii.PushId($"order{c.Cid}");
            Ui.NextRow();
            Ui.NextColumn();
            RightNumber(i + 1, Dim);
            Ui.NextColumn();
            CharacterCell(c.Character, c.World);
            if (c.Account.Length > 0)
            {
                ImGui.SameLine();
                Colored(Dim, $"· {c.Account}");
            }
            Ui.NextColumn();
            using (ImRaii.Disabled(i == 0))
            {
                if (ImGui.SmallButton("Up")) (draft[i - 1], draft[i]) = (draft[i], draft[i - 1]);
                ImGui.SameLine();
                if (ImGui.SmallButton("To top")) { draft.RemoveAt(i); draft.Insert(0, c); }
            }
            ImGui.SameLine();
            using (ImRaii.Disabled(i == draft.Count - 1))
            {
                if (ImGui.SmallButton("Down")) (draft[i + 1], draft[i]) = (draft[i], draft[i + 1]);
            }
        }
    }

    // ---- Gil & Market's unsold days, the Income window ----

    private int staleDaysDraft;

    private void StaleDaysEditor(int current)
    {
        if (staleDaysDraft == 0) staleDaysDraft = current;
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        Ui.InputInt("days counts as unsold for a while##staleDays", ref staleDaysDraft, 1);
        staleDaysDraft = Math.Clamp(staleDaysDraft, 1, FleetEdits.MaxStaleDays);
        ImGui.SameLine();
        using (ImRaii.Disabled(reader.Editing || staleDaysDraft == current))
        {
            if (ImGui.Button("Save##staleDays"))
            {
                var days = staleDaysDraft;
                reader.Edit("the unsold days", c => FleetEdits.SaveStaleDays(c, days));
            }
        }
        EditLine();
    }

    private void IncomeWindowEditor(int current)
    {
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        using (var combo = Ui.Combo("Income over##incomeWindow", $"{current} days"))
        {
            if (combo)
                foreach (var d in FleetEdits.WindowChoices)
                    if (Ui.Selectable($"{d} days", d == current) && d != current)
                        reader.Edit("the Income window", c => FleetEdits.SaveIncomeWindow(c, d));
        }
        Hover("How many days the Income page (and Loot per Voyage) counts" + (WithApp ? ", for the app too." : "."));
        ImGui.SameLine();
        EditLine();
    }

    // ---- Supplies: the days use is measured over and how many days to buy for (as the app's Supplies tab) ----

    private int restockDraft = -1;
    private int restockShown = -1;

    // On its own line, each label before what it sets: "Use per day measured over [14 days] · Buy enough for [14] days [Save]".
    private void SupplyDaysEditor(int window, int target)
    {
        ImGui.AlignTextToFramePadding();
        Ui.Text("Use per day measured over");
        Hover("How many days back the use per day is measured" + (WithApp ? ", for the app too." : "."));
        ImGui.SameLine();
        ImGui.SetNextItemWidth(100 * ImGuiHelpers.GlobalScale);
        using (var combo = Ui.Combo("##supplyWindow", $"{window} days"))
        {
            if (combo)
                foreach (var d in FleetEdits.WindowChoices)
                    if (Ui.Selectable($"{d} days", d == window) && d != window)
                        reader.Edit("the Supplies days", c => FleetEdits.SaveSupplyDays(c, d, target));
        }
        ImGui.SameLine();
        Colored(Dim, "·");
        ImGui.SameLine();
        Ui.Text("Buy enough for");
        Hover($"The To buy column: enough supplies to last this many days (1 to {FleetEdits.MaxRestockDays})" + (WithApp ? ", for the app too." : "."));
        // The box follows what was read until it's changed here.
        if (restockShown != target) { restockShown = target; restockDraft = target; }
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90 * ImGuiHelpers.GlobalScale);
        Ui.InputInt("##restockDays", ref restockDraft, 1);
        restockDraft = Math.Clamp(restockDraft, 1, FleetEdits.MaxRestockDays);
        ImGui.SameLine();
        Ui.Text("days");
        if (restockDraft != target)
        {
            ImGui.SameLine();
            using (ImRaii.Disabled(reader.Editing))
            {
                if (ImGui.Button("Save##restockDays"))
                {
                    var days = restockDraft;
                    reader.Edit("the Supplies days", c => FleetEdits.SaveSupplyDays(c, window, days));
                }
            }
        }
        if (reader.Editing || reader.LastEdit != null)
        {
            ImGui.SameLine();
            EditLine();
        }
    }

    // ---- Removing a PC (PCs page) ----

    private string? pcToRemove;

    private void RemovePcCell(PcRow p)
    {
        var key = $"{p.Pc}|{p.Account}";
        if (pcToRemove != key)
        {
            if (ImGui.SmallButton($"Remove##pc{key}")) pcToRemove = key;
            Hover("A PC no longer used (or renamed): its row goes. If the plugin still runs there, it comes back at its next sync.");
            return;
        }
        using (ImRaii.PushColor(ImGuiCol.Button, Red with { W = 0.6f }))
        {
            if (ImGui.SmallButton($"Remove it##pcSure{key}"))
            {
                var (pc, account) = (p.Pc, p.Account);
                reader.Edit($"removing {pc}", async c => (await FleetEdits.RemovePc(c, pc, account)).Error);
                pcToRemove = null;
            }
        }
        ImGui.SameLine();
        if (ImGui.SmallButton($"Keep##pcKeep{key}")) pcToRemove = null;
    }

    // ---- Ticking parts off (Shopping List) ----

    private readonly Dictionary<int, (long Made, DateTime At)> madePending = new();
    private readonly Dictionary<int, int> madeTyping = new();

    private long MadeOf(ShopRow p) =>
        madePending.TryGetValue(p.ItemId, out var m) && !(reader.Shopping.At > m.At) ? m.Made : p.Made;

    private void MadeCell(ShopRow p)
    {
        // What's being typed stays until the box is left (then saved).
        var made = madeTyping.TryGetValue(p.ItemId, out var typing) ? typing : (int)MadeOf(p);
        ImGui.SetNextItemWidth(60 * ImGuiHelpers.GlobalScale);
        if (Ui.InputInt($"##made{p.ItemId}", ref made, 0)) madeTyping[p.ItemId] = made;
        if (ImGui.IsItemDeactivatedAfterEdit())
        {
            madeTyping.Remove(p.ItemId);
            SaveMade(p, made);
        }
        ImGui.SameLine();
        Ui.Text($"of {Number(p.Crafts)}");
        ImGui.SameLine();
        var all = MadeOf(p) >= p.Crafts;
        if (Ui.Checkbox($"all made##allMade{p.ItemId}", ref all)) SaveMade(p, all ? (int)p.Crafts : 0);
        Hover("Tick parts off as you craft them; " + (WithApp ? "every PC sees it (in the app too)." : "every game on this PC sees it.") + " Once the plugin sees a crafted part in bags, a retainer or the FC chest, it leaves the list by itself.");
    }

    private void SaveMade(ShopRow p, int made)
    {
        made = (int)Math.Clamp(made, 0, p.Crafts);
        madePending[p.ItemId] = (made, DateTime.Now);
        var (item, crafts, ahead) = (p.ItemId, p.Crafts, partsAheadDays);
        reader.Edit($"{p.Name} made", c => FleetEdits.SaveShoppingProgress(c, item, made, crafts, ahead));
    }
}
