# XIV Fleet Companion — Dalamud Plugin

Dalamud plugin that syncs FFXIV fleet data to a Postgres database. The
[desktop app](https://github.com/CrownedPandaXIV/XIVFleetCompanion-App) reads that
database to show rosters, submarines, parts, inventory and more.

The plugin only captures raw data. Anything computed (rank progress, Gil/day,
housing eligibility display, etc.) is left to the app.

## Data sources

- **AutoRetainer** (via `AutoRetainerAPI`): characters, retainers, submarines, Gil, Ceruleum, repair kits
- **AllaganTools**: personal, retainer and Free Company chest inventory. FC chest data is only available after the in-game FC chest UI has been opened, so it can be empty on a given sync.
- **FCTracker**: Free Company housing, founding date and eligibility data (read from its config file)

## Usage

- `/xivfleet` opens the main window; the plugin installer's config button opens settings.
- Enter Postgres host, port, database, user and password in the settings window. Credentials are stored in Windows Credential Manager, with separate entries for a local and a remote connection.
- The plugin syncs on the interval set in settings and runs a daily retention/downsampling cleanup.

## History for charts

Besides the per-sync snapshot table, the plugin records selected values into a general
history table (`companion_metric_history`) so they can be charted over time. A value is
stored only when it changes, plus an hourly "still the same" marker, so slow-moving values
stay small. Currently recorded:

| Metric | Belongs to | Notes |
| --- | --- | --- |
| `gil`, `ceruleum`, `repair_kits` | character | |
| `fc_points` | Free Company | from FCTracker |
| `item_qty:<id>` for the eight salvage items (22500-22507) | character and Free Company | character = bags + retainers; FC = chest, only when AllaganTools has chest data |

**Setup, once:** run `sql/001_metric_history.sql` in the database the plugin writes to. Until
that has been run, the plugin logs a warning each sync and skips metric history (everything
else keeps working). Optionally run `sql/002_backfill_metrics_from_snapshots.sql` afterwards
to copy the existing gil, ceruleum and repair kit history into the new table.

To record something new, add another `AddMetric(...)` call in `Plugin.cs`; no schema change
is needed.

## Building

Requires XIVLauncher, FFXIV and Dalamud installed, and the .NET SDK.

1. Clone with submodules: `git clone --recurse-submodules`
2. Open `XIVFleetCompanion.slnx` in Visual Studio or Rider and build.
3. Add the built `XIVFleetCompanion.dll` under Dev Plugin Locations in `/xlsettings`, then enable it in `/xlplugins`.

Licensed under the terms in `LICENSE.md`.
