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

## Building

Requires XIVLauncher, FFXIV and Dalamud installed, and the .NET SDK.

1. Clone with submodules: `git clone --recurse-submodules`
2. Open `XIVFleetCompanion.slnx` in Visual Studio or Rider and build.
3. Add the built `XIVFleetCompanion.dll` under Dev Plugin Locations in `/xlsettings`, then enable it in `/xlplugins`.

Licensed under the terms in `LICENSE.md`.
