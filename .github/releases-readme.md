# XIV Fleet Companion

Your whole FFXIV submarine and retainer fleet in one place, across every character, account and PC: which subs are
back, what they brought home, what's running low, which parts to build next, what your retainers are up to and what
you earned.

- **The plugin** runs in the game. It keeps your fleet up to date while you play, or while AutoRetainer runs your
  characters, and shows all of it in its own window.
- **The app** (Windows, optional) shows the same fleet on your desktop, outside the game.

**Where your fleet lives** is your choice, and the plugin asks the first time:

| | Just this PC | Shared between PCs |
| --- | --- | --- |
| What you install | just the plugin | the plugin, and PostgreSQL (the database) on one PC |
| Where you see it | in the game, and in the app on this PC if you want it | in the game on every PC, and in the app |
| Good for | playing on one PC | playing on more than one PC |

For **Shared between PCs**, the plugin's Settings → **Database tools** or the app's setup screen installs PostgreSQL
for you. If your PCs aren't on the same home network, they also install **Tailscale** (free) so the PCs can reach the
database.

## Install the plugin

1. In the game, type `/xlsettings` and open the **Experimental** tab.
2. Under **Custom Plugin Repositories**, paste this address, click **+**, tick **Enabled**, then click **Save and Close**:

   ```
   https://raw.githubusercontent.com/CrownedPandaXIV/XIVFleetCompanion-Releases/main/repo.json
   ```

3. Do the same for AutoRetainer's address (in the table below), which the plugin needs.
4. Type `/xlplugins`, search for **XIV Fleet Companion**, and click **Install**. Install **AutoRetainer** too, and
   whichever of the others you want.
5. Type `/xivfleet`. The first time, it asks where your fleet should live (see above).

Dalamud keeps the plugins up to date for you.

## Install the app (optional)

1. Download `XIV-Fleet-Companion-Setup-<version>.exe` from the
   [latest release](https://github.com/CrownedPandaXIV/XIVFleetCompanion-Releases/releases/latest).
2. Run it. If Windows says "Windows protected your PC", click **More info**, then **Run anyway**.
3. Start the app. On a PC where the plugin keeps the fleet (Just this PC), it opens it by itself. Otherwise it asks what
   the PC is for and sets it up, installing PostgreSQL and Tailscale where they're needed.

The app updates itself. When a new version is ready, a bar at the top offers to restart into it.

**Other plugins it works with:**

| Plugin | | Where to get it |
| --- | --- | --- |
| **AutoRetainer** | required | Add `https://love.puni.sh/ment.json` in the Experimental tab, as above |
| **Allagan Tools** | recommended: adds your inventories and retainers' items | Dalamud's own plugin list |
| **FC Tracker** | optional: Free Company houses | Add `https://puni.sh/api/repository/erdelf` in the Experimental tab |
| **Submarine Tracker** | optional: what each voyage brought back | Dalamud's own plugin list |

Not sure everything's working? Type `/xivfleet check` in the game.

## Try new versions first

Want new versions before everyone else? They can still have problems, so this is only for whoever wants to try things
first.

- **App:** in **Settings → General → Updates**, tick **Get test versions**. That PC then gets test versions (numbered
  like `1.6.0-test.12`) and every normal update too. Untick it to go back; you keep the version you have until a normal
  version is newer. The newest test version is also on the
  [testing release](https://github.com/CrownedPandaXIV/XIVFleetCompanion-Releases/releases/tag/testing), if you'd
  rather install it by hand.
- **Plugin:** type `/xlsettings`, open the **Experimental** tab and tick **Get plugin testing builds**, then **Save and
  Close**. Type `/xlplugins`, open **Installed Plugins**, right-click **XIV Fleet Companion** and choose to receive its
  testing versions. Dalamud then offers its test builds when there are any, and the normal version otherwise.

## Found a problem?

Report it on the app's [GitHub page](https://github.com/CrownedPandaXIV/XIVFleetCompanion-App/issues/new/choose), for the app and the plugin alike. In the app, **Settings → Setup Help →
Found a problem?** has the app's version to copy and its log. For the plugin, paste the **Copy results** of
`/xivfleet check`.

## Thanks

XIV Fleet Companion couldn't work without these plugins. A big thank you to the people who make them: kawaii,
NightmareXIV and the Puni.sh team (AutoRetainer), Critical_Impact (Allagan Tools), erdelf (FC Tracker) and Infi
(Submarine Tracker).
