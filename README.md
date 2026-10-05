# XIV Fleet Companion — Dalamud Plugin

Dalamud plugin that syncs FFXIV fleet data to a Postgres database. The
[desktop app](https://github.com/CrownedPandaXIV/XIVFleetCompanion-App) reads that
database to show rosters, submarines, parts, inventory and more.

The plugin only captures raw data. Anything computed (rank progress, Gil/day,
housing eligibility display, etc.) is left to the app.

## Data sources

- **AutoRetainer** (via `AutoRetainerAPI`): characters, retainers, submarines, Gil, Ceruleum, repair kits
- **AllaganTools**: personal, retainer and Free Company chest inventory. FC chest data is only available after the in-game FC chest UI has been opened, so it can be empty on a given sync. When AllaganTools is not running, the stored inventories are kept as they were rather than emptied.
- **FCTracker**: Free Company housing, founding date and eligibility data (read from its config file)

## Usage

- `/xivfleet` opens the main window; the plugin installer's config button opens settings.
- Enter Postgres host, port, database, user and password in the settings window. Credentials are stored in Windows Credential Manager, with separate entries for a local and a remote connection. The form shows the saved details (never the password); leave the password box empty to keep the saved one. "Clear Saved Credentials" asks for a second click.
- The plugin syncs on the interval set in settings, and also about 5 seconds after a character logs
  out (AutoRetainer logs a character out once it has finished with it), at most once every 30 seconds;
  "Also sync right after a character logs out" in settings turns that off. Inventories, FC chests, submarines, retainers and
  housing are written only when they changed (and in full once an hour); each character's current
  row and the chart history every sync (the chart history stores only values that changed).
  Dismissed retainers, chests of Free Companies none of your characters is in any more, and the old
  Free Company details of a character that left its FC are removed as they are noticed.
- **Characters** in settings chooses which characters are synced: every character AutoRetainer knows
  except the ones unticked (the default), or only the ones ticked, for an account where only a few of
  many characters should reach the app. "Tick only those with subs" ticks just the characters with a
  workshop. Left-out characters are not read or sent at all; anything they sent before can be removed
  with the app's Remove button on the Roster.

## Current state per character

From 0.3.0 the plugin also keeps `companion_character_current`: one row per character with what the
app shows (name, world, account, gil, ceruleum, repair kits, retainers, submarines, sub slots, Free
Company), when it was first seen and when it last synced. Name, world, account label and Free Company
changes are logged in `companion_character_changes` automatically. **Run `sql/004_character_current.sql`
once before updating the plugin to 0.3.0**; it creates both tables and fills them from the existing
snapshot history (a minute or two on a few million rows).

## The old snapshot history (removed in 0.4.0)

Up to 0.3.x the plugin also wrote `companion_character_snapshot`: a full row per character on every
sync, about 900 MB after a few months. Everything worth keeping from it is now elsewhere (current
values in `companion_character_current`, values over time in the chart history, identity changes in
`companion_character_changes`), so from 0.4.0 it is no longer written, and the daily
retention/downsampling clean-up that kept it in check is gone too. To remove the table:

1. Update the plugin to 0.4.0 on every PC that runs it and let it sync once.
2. Save the table to a file: the app's `backup\save-character-snapshot.ps1` (it checks the file too).
3. Run `sql/005_remove_character_snapshot.sql`. It refuses, changing nothing, if a plugin older than
   0.4.0 still writes the table or a character has no current row.

`sql/002` and `sql/003` only apply to databases that still have the old table.

## Submarine slots

From 0.5.0 each stored sub has its workshop slot (1-4), so renaming a sub does not break anything:
the app finds each slot's sub by slot, and a renamed sub's Craft? setting moves to its new name. Run
`sql/006_submarine_slot.sql` once to add the column (the plugin writes subs without slots until then).

## History for charts

The plugin records selected values into a general history table (`companion_metric_history`) so they can be charted over time. A value is
stored only when it changes, plus an hourly "still the same" marker, so slow-moving values
stay small. Currently recorded:

| Metric | Belongs to | Notes |
| --- | --- | --- |
| `gil`, `ceruleum`, `repair_kits` | character | |
| `retainer_count`, `submarine_count`, `num_sub_slots` | character | from 0.3.0; `sql/004` copies the earlier changes from the snapshot history |
| `fc_points` | Free Company, and the FC leader's character when it is one of yours | from FCTracker; labelled with the leader's name (on the FC) or the FC's name (on the leader). A leader change is recorded even if the points are unchanged |
| `item_qty:<id>` for the eight salvage items (22500-22507) | character and Free Company | character = bags + retainers; FC = chest, only when AllaganTools has chest data |

**Setup, once:** run `sql/001_metric_history.sql` in the database the plugin writes to. Until
that has been run, the plugin logs a warning each sync and skips metric history (everything
else keeps working). Optionally run `sql/002_backfill_metrics_from_snapshots.sql` afterwards
to copy the existing gil, ceruleum and repair kit history into the new table.

To record something new, add another `AddMetric(...)` call in `Plugin.cs`; no schema change
is needed.

## Versioning

The plugin uses [Semantic Versioning](https://semver.org) (`MAJOR.MINOR.PATCH`), set as
`<Version>` in `XIVFleetCompanion/XIVFleetCompanion.csproj`. It shows in the window titles, the
plugin installer and the log.

- **PATCH** (0.1.0 to 0.1.1): bug fixes, no new behavior.
- **MINOR** (0.1.0 to 0.2.0): new data recorded or new features.
- **MAJOR** (0.x to 1.0, then 1.0 to 2.0): 1.0 marks the first stable release; after that,
  a change that needs a database update or a newer app first.

To release: change `<Version>`, add a section to `CHANGELOG.md`, merge. The tag `v<new>` and the GitHub release (with that
changelog section as its description) are then created automatically by `.github/workflows/release.yml`. The plugin and the app are versioned separately; the changelog notes when one
needs the other updated.

## Building

Requires XIVLauncher, FFXIV and Dalamud installed, and the .NET SDK.

1. Clone with submodules: `git clone --recurse-submodules`
2. Open `XIVFleetCompanion.slnx` in Visual Studio or Rider and build, or open the repository folder in VS Code and press `Ctrl+Shift+B` (`.vscode/tasks.json` builds Release; Terminal > Run Task also has a Debug build). From a terminal: `dotnet build XIVFleetCompanion/XIVFleetCompanion.csproj -c Release`.
3. Add the built `XIVFleetCompanion.dll` under Dev Plugin Locations in `/xlsettings`, then enable it in `/xlplugins`.

Licensed under the terms in `LICENSE.md`.
