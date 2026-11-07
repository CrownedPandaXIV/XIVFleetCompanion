# XIV Fleet Companion (Dalamud plugin)

Your whole fleet in the game. While you play, or while AutoRetainer runs your characters, it quietly keeps your
characters, subs, retainers and inventories, and shows them in its own window: subs and their routes, supplies, parts,
retainers, Free Companies, inventories, income, the market and how it all changes over time.

Keep it all on one PC with nothing else to install, or share it with your other PCs and the
[XIV Fleet Companion app](https://github.com/CrownedPandaXIV/XIVFleetCompanion-App), which shows the same fleet on
your desktop.

## What you need

| Plugin | | What it adds | Where to get it |
| --- | --- | --- | --- |
| **AutoRetainer** | required | Your characters, retainers, subs, gil, ceruleum and repair kits. | Repository below |
| **Allagan Tools** | recommended | Everything in your bags, your retainers' and the FC chest. | Dalamud's own plugin list |
| **FC Tracker** | optional | Free Company houses, and when each was last entered. A recent FC Tracker is asked directly; an older one is read from its saved file. | Repository below |
| **Submarine Tracker** | optional | What every voyage brought back, for the app's income per voyage. | Dalamud's own plugin list |

Your fleet lives either in one file on your PC (**Just this PC**, nothing else to install) or in a PostgreSQL database
shared with the app and your other PCs (**Shared between PCs**). The plugin asks which the first time.

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

Type `/xivfleet`. The first time, it asks **where your fleet data should live**:

| | Just this PC | Shared between PCs |
| --- | --- | --- |
| What you install | nothing else | the app and PostgreSQL (the app's Database Helper sets it up) |
| Where the fleet is | `%APPDATA%\XIV Fleet Companion\fleet.db`, shared by every game on this PC | a database your PCs and the app all use |
| Where you see it | in the game | in the game and in the app |
| Backups | a copy a day in `Backups` beside the file, the last 7 kept, plus **Back up now** on the Status page | the app's backups |

The file isn't allowed in a OneDrive, Dropbox, Google Drive or network folder: those copy it while a game is writing
and can damage it. A setup that already had a database connection before 2.2 isn't asked and keeps using it.

Then open the settings:

- **Database** (Shared between PCs only): enter the same address, port, database name, username and password as in the app. The password is
  kept in Windows Credential Manager and never shown again. Leave the box empty to keep the saved one.
- **Characters:** sync everyone AutoRetainer knows (the default), or only the characters you tick. "Tick only those
  with subs" is handy for accounts where only a few characters matter.
- **Account Label:** a name for this setup's account (for example, the account's name). With more than one account,
  give each its own setup of AutoRetainer and this plugin, each with its own label.

**More than one account:** each setup notes when a character logs in on it, and only the setup a character last
logged in on syncs it. So if one setup's AutoRetainer still has an old copy of a character that's now on another
account, it leaves that character alone instead of overwriting it. Characters that haven't logged in anywhere yet
are synced by every setup that has them, as before. There's no time limit, so a long break changes nothing.

### Shared between PCs

The database is set up by the app (or its Database Helper) the first time it connects, so there's nothing to run on it.
After that, the plugin keeps it up to date itself, as the app does.

If you move the database to another PC (the app's Settings → Database → **Move the database to this PC**), the plugin
follows it by itself at its next sync: it switches to the new address and saves it in its settings, keeping the same
database name, username and password. While the app is copying the database, the plugin waits and tries again every
minute, so nothing is lost mid-copy.

### Just this PC

There's nothing to set up: the file is made the first time the game reads or syncs, and every game on the PC uses the
same one. The plugin keeps it up to date itself.

- **Backups** are in the `Backups` folder beside the file, one a day, the last 7 kept. The Status page shows the newest
  and has **Back up now**. To go back to one, close every game, then copy it over `fleet.db`.
- **Settings** shared by the whole fleet (what you run, the warnings, Live Market Prices, the repair kit cost, Fight
  Club's data) are in the plugin's Settings, saved in the file.
- **Without the app**, the plugin does what the app would: it saves each sub's Next Rank date once an hour for Route
  Optimization's forecast line, and the Supplies page's days are changed on that page.
- **Moving your fleet** between Just this PC and a shared database, either way, is in the plugin's Settings → **Move
  your fleet**. Every table is copied and compared before anything switches, it never copies into a place that already
  has a fleet, and the side it came from is left as it was. Every game on the PC follows at its next sync.

## Using it

- **`/xivfleet`** opens the main window. It shows when the last sync was and, if syncing isn't getting through (for
  example the PC with the database is off), why. The Status page shows where your fleet is kept.
- **`/xivfleet check`** (or **Check what I can see**) shows, character by character, what the plugin can see and
  what you need to open in the game to fill any gaps. For example, Allagan Tools only sees a retainer's items after
  you've opened that retainer at a summoning bell. **Copy results** copies it so you can share it, and the PCs page (and the app's)
  shows it too.

The plugin syncs on the interval in its settings, and also a few seconds after AutoRetainer finishes with a character.
To keep things light, it only sends what's changed (plus everything once an hour).

## What it records

- **Each character:** name, world, account, gil, ceruleum, repair kits, retainers, subs and Free Company.
- **Logins:** which setup (PC and account label) each character last logged in on, and when.
- **Inventories:** bags, retainers and FC chests, from Allagan Tools.
- **Subs and retainers:** levels, parts, routes and ventures, plus what each voyage and venture brought back.
- **Market listings** your retainers have up for sale.
- **Each PC's status:** plugin version, last sync, which other plugins are running, what it couldn't see, and (on a
  shared database) the database login it uses, so a database move can check every PC will be let in.
- **Values over time** for the Trends charts: gil, ceruleum, repair kits, FC points and salvage. Only changes are
  stored, so the history stays small.

---

## Found a problem?

Report it on the app's [GitHub page](https://github.com/CrownedPandaXIV/XIVFleetCompanion-App/issues/new/choose), which is for the plugin too. Include the plugin's version (in the
title of its window), and the **Copy results** of `/xivfleet check` if it's about what the plugin sees. The app's
**Settings → Setup Help → Found a problem?** has the app's version and its log.

## Thanks

XIV Fleet Companion couldn't work without these plugins. A big thank you to the people who make them:

- **AutoRetainer** by kawaii, NightmareXIV and the Puni.sh team
- **Allagan Tools** by Critical_Impact
- **FC Tracker** by erdelf
- **Submarine Tracker** by Infi

## License

The plugin is free software under the [GNU Affero General Public License v3](LICENSE.md), the same as the app. You can
use it, share it and change it. If you share a changed version, share its code under the same license. It comes with
no warranty.

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

**A test build first:** push the branch to `testing` (`git push origin dev:testing --force`). GitHub builds it as
`<version>.<run>` (1.0.2.7) and adds it to `repo.json` as the plugin's testing version, so only players who opted in to
its testing builds in Dalamud get it (`.github/workflows/test-release.yml`). The next normal release of that version is
numbered above the last test build, so they get that too. `tests/scripts/release-scripts.sh` checks the scripts.

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
