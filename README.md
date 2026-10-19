# XIV Fleet Companion (Dalamud plugin)

The in-game half of XIV Fleet Companion. While you play, or while AutoRetainer runs your characters, it quietly sends
your characters, subs, retainers and inventories to a database. The
[XIV Fleet Companion app](https://github.com/CrownedPandaXIV/XIVFleetCompanion-App) then shows your whole fleet from
there, across every account and PC.

The plugin just collects. All the working out (income, rank-ups, what to build, alerts) happens in the app.

## What you need

| Plugin | | What it adds | Where to get it |
| --- | --- | --- | --- |
| **AutoRetainer** | required | Your characters, retainers, subs, gil, ceruleum and repair kits. | Repository below |
| **Allagan Tools** | recommended | Everything in your bags, your retainers' and the FC chest. | Dalamud's own plugin list |
| **FC Tracker** | optional | Free Company houses, and when each was last entered. A recent FC Tracker is asked directly; an older one is read from its saved file. | Repository below |
| **Submarine Tracker** | optional | What every voyage brought back, for the app's income per voyage. | Dalamud's own plugin list |

You also need the app connected to a PostgreSQL database. The plugin writes to the same database.

## Install

1. In the game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste each of these addresses into the empty box and click **+**:

   XIV Fleet Companion:
   ```
   https://raw.githubusercontent.com/CrownedPandaXIV/XIVFleetCompanion-Releases/main/repo.json
   ```
   AutoRetainer:
   ```
   https://love.puni.sh/ment.json
   ```
   FC Tracker (optional):
   ```
   https://puni.sh/api/repository/erdelf
   ```

3. Tick **Enabled** next to each, then click **Save and Close**.
4. Type `/xlplugins` and install **XIV Fleet Companion**, **AutoRetainer**, and whichever of **Allagan Tools**,
   **FC Tracker** and **Submarine Tracker** you want.

Dalamud keeps them all up to date from then on.

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

If you move the database to another PC (the app's Settings → Database → **Move the database to this PC**), the plugin
follows it by itself at its next sync: it switches to the new address and saves it in its settings, keeping the same
database name, username and password. While the app is copying the database, the plugin waits and tries again every
minute, so nothing is lost mid-copy.

## Using it

- **`/xivfleet`** opens the main window. It shows when the last sync was and, if syncing isn't getting through (for
  example the PC with the database is off), why.
- **`/xivfleet check`** (or **Check what I can see**) shows, character by character, what the plugin can see and
  what you need to open in the game to fill any gaps. For example, Allagan Tools only sees a retainer's items after
  you've opened that retainer at a summoning bell. **Copy results** copies it so you can share it, and the app's PCs
  tab shows it too.

The plugin syncs on the interval in its settings, and also a few seconds after AutoRetainer finishes with a character.
To keep things light, it only sends what's changed (plus everything once an hour).

## What it records

- **Each character:** name, world, account, gil, ceruleum, repair kits, retainers, subs and Free Company.
- **Inventories:** bags, retainers and FC chests, from Allagan Tools.
- **Subs and retainers:** levels, parts, routes and ventures, plus what each voyage and venture brought back.
- **Market listings** your retainers have up for sale.
- **Each PC's status:** plugin version, last sync, which other plugins are running, what it couldn't see, and the
  database login it uses (so a database move can check every PC will be let in).
- **Values over time** for the app's charts: gil, ceruleum, repair kits, FC points and salvage. Only changes are
  stored, so the history stays small.

---

## Thanks

XIV Fleet Companion couldn't work without these plugins. A big thank you to the people who make them:

- **AutoRetainer** by kawaii, NightmareXIV and the Puni.sh team
- **Allagan Tools** by Critical_Impact
- **FC Tracker** by erdelf
- **Submarine Tracker** by Infi

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
everywhere.

### Adding a chart value

Add another `AddMetric(...)` call in `Plugin.cs`. The database needs no change.
