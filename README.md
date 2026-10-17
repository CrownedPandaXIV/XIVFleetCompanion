# XIV Fleet Companion (Dalamud plugin)

The in-game half of XIV Fleet Companion. While you play, or while AutoRetainer runs your characters, it quietly sends
your characters, subs, retainers and inventories to a database. The
[XIV Fleet Companion app](https://github.com/CrownedPandaXIV/XIVFleetCompanion-App) then shows your whole fleet from
there, across every account and PC.

The plugin just collects. All the working out (income, rank-ups, what to build, alerts) happens in the app.

## What you need

| Plugin | | What it adds |
| --- | --- | --- |
| **AutoRetainer** | required | Your characters, retainers, subs, gil, ceruleum and repair kits. |
| **AllaganTools** | recommended | Everything in your bags, your retainers' and the FC chest. |
| **FCTracker** | optional | Free Company houses, and when each was last entered. A recent FCTracker is asked directly; an older one is read from its saved file. |
| **SubmarineTracker** | optional | What every voyage brought back, for the app's income per voyage. |

You also need the app connected to a PostgreSQL database. The plugin writes to the same database.

## Install

1. In the game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this address, tick **Enabled**, then click **Save and Close**:

   ```
   https://raw.githubusercontent.com/CrownedPandaXIV/XIVFleetCompanion-Releases/main/repo.json
   ```

3. Type `/xlplugins`, search for **XIV Fleet Companion**, and click **Install**.

Dalamud keeps it up to date from then on.

If you used a copy you built yourself, remove it from **Dev Plugin Locations** in `/xlsettings` first. Your settings and
saved password carry over.

## Set it up

Type `/xivfleet` and open the settings:

- **Database:** enter the same address, port, database name, username and password as in the app. The password is
  kept in Windows Credential Manager and never shown again. Leave the box empty to keep the saved one.
- **Characters:** sync everyone AutoRetainer knows (the default), or only the characters you tick. "Tick only those
  with subs" is handy for accounts where only a few characters matter.

The database itself is set up by the app the first time it connects, so there's nothing to run on it. If the plugin's
log says the database is missing something, open the app once and it'll sort it out.

## Using it

- **`/xivfleet`** opens the main window.
- **`/xivfleet check`** (or **Check what I can see**) shows, character by character, what the plugin can see and
  what you need to open in the game to fill any gaps. For example, AllaganTools only sees a retainer's items after
  you've opened that retainer at a summoning bell. **Copy results** copies it so you can share it, and the app's PCs
  tab shows it too.

The plugin syncs on the interval in its settings, and also a few seconds after AutoRetainer finishes with a character.
To keep things light, it only sends what's changed (plus everything once an hour).

## What it records

- **Each character:** name, world, account, gil, ceruleum, repair kits, retainers, subs and Free Company.
- **Inventories:** bags, retainers and FC chests, from AllaganTools.
- **Subs and retainers:** levels, parts, routes and ventures, plus what each voyage and venture brought back.
- **Market listings** your retainers have up for sale.
- **Each PC's status:** plugin version, last sync, which other plugins are running, and what it couldn't see.
- **Values over time** for the app's charts: gil, ceruleum, repair kits, FC points and salvage. Only changes are
  stored, so the history stays small.

---

## For developers

### Building

You need XIVLauncher, the game, Dalamud and the .NET SDK.

1. Clone with submodules: `git clone --recurse-submodules`
2. Open `XIVFleetCompanion.slnx` in Visual Studio or Rider and build.
3. In `/xlsettings`, add the built `XIVFleetCompanion.dll` under **Dev Plugin Locations**, then enable it in
   `/xlplugins`.

### Tests

The database-writing code has its own tests in [tests/FleetWriterTests](tests/FleetWriterTests/README.md). They run on
GitHub for every pull request, along with a full build.

### Releasing a new version

1. Raise `<Version>` in `XIVFleetCompanion/XIVFleetCompanion.csproj`.
2. Add a section for it at the top of `CHANGELOG.md`.
3. Merge to `master`.

GitHub then tags the release, builds the plugin and publishes it to the plugin list in
[XIVFleetCompanion-Releases](https://github.com/CrownedPandaXIV/XIVFleetCompanion-Releases), where Dalamud picks it up.
It also keeps that page's text the same as `.github/releases-readme.md`. Both need the `RELEASES_TOKEN` secret: a
token that can write to that repository.

Version numbers follow [Semantic Versioning](https://semver.org):

- **0.1.0 → 0.1.1** for fixes,
- **0.1.0 → 0.2.0** for new data or features,
- **1.0** for the first stable release.

The plugin and the app have separate version numbers. The changelog says when one needs a newer version of the other.

### Database changes

The app owns the database layout. A new table or column goes in the app's `db/updates/` folder, and the app applies it
everywhere. The `sql/` files here are the history from before that. They're only needed for a database still on the old
`companion_character_snapshot` table, from before plugin 0.4.0.

### Adding a chart value

Add another `AddMetric(...)` call in `Plugin.cs`. The database needs no change.
