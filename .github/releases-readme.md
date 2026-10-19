# XIV Fleet Companion

Your whole FFXIV submarine and retainer fleet in one window, across every account and PC. There are two parts:

- **The plugin** runs in the game on each PC that plays. It sends your characters, subs, retainers and inventories to
  a database.
- **The app** (Windows) shows it all: what's back, what's running low, what to build next, what you earned. It can ping
  you on Discord too.

You'll also need **PostgreSQL** (the database) on one PC. If your PCs aren't on the same home network, you'll need
**Tailscale** (free) so they can reach it.

## Install the app

1. Download `XIV-Fleet-Companion-Setup-<version>.exe` from the
   [latest release](https://github.com/CrownedPandaXIV/XIVFleetCompanion-Releases/releases/latest).
2. Run it. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**.
3. Start the app. The first time, it asks what the PC is for and sets it up, installing PostgreSQL and Tailscale where
   they're needed.

The app updates itself. When a new version is ready, a bar at the top offers to restart into it.

## Install the plugin

1. In the game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this address, tick **Enabled**, then click **Save and Close**:

   ```
   https://raw.githubusercontent.com/CrownedPandaXIV/XIVFleetCompanion-Releases/main/repo.json
   ```

3. Type `/xlplugins`, search for **XIV Fleet Companion**, and click **Install**.
4. Type `/xivfleet`, open its settings, and enter the same database details as the app.

Dalamud keeps the plugin up to date for you.

**Other plugins it works with:**

| Plugin | | Where to get it |
| --- | --- | --- |
| **AutoRetainer** | required | Add `https://love.puni.sh/ment.json` in the Experimental tab, as above |
| **Allagan Tools** | recommended: adds your inventories and retainers' items | Dalamud's own plugin list |
| **FC Tracker** | optional: Free Company houses | Add `https://puni.sh/api/repository/erdelf` in the Experimental tab |
| **Submarine Tracker** | optional: what each voyage brought back | Dalamud's own plugin list |

Not sure everything's working? Type `/xivfleet check` in the game.

## Thanks

XIV Fleet Companion couldn't work without these plugins. A big thank you to the people who make them: kawaii,
NightmareXIV and the Puni.sh team (AutoRetainer), Critical_Impact (Allagan Tools), erdelf (FC Tracker) and Infi
(Submarine Tracker).
