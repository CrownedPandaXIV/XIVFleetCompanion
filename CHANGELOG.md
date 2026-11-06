# Changelog

All notable changes to the plugin are recorded here. Versions follow
[Semantic Versioning](https://semver.org): MAJOR.MINOR.PATCH.

## 2.0.0 - unreleased

In progress: the fleet inside the game. Built in stages, each tried as a test build first.

- **Stage 1: speed test.** Main window → **Speed test (2.0)** (or `/xivfleet speedtest`) times reading from the
  database what the in-game Summary, Subs and Supplies windows will need: connecting, characters, subs, retainers,
  ceruleum and repair kits in FC chests, their use over the last 14 days, and the PCs. Three rounds, with the first,
  best and worst time of each, and the longest game frame meanwhile to show it doesn't stutter. It runs in the
  background, only reads, and **Copy results** copies it all to share.
- **Stage 2: the database views.** The speed test also times reading the ready-made views app 1.8.0 adds to the
  database (subs, supplies and the summary), which work things out in the database instead of in the plugin. With an
  older app they show as failed.
- **Stage 3: one main window with tabs.** `/xivfleet` (or the plugin's button in Dalamud) opens one window with
  **Summary**, **Subs**, **Supplies**, **Status** and **Settings** tabs; `/xivfleet subs` (or summary, supplies,
  status, settings) opens it at that tab, and Dalamud's settings button opens the Settings tab. Look only for now.
  - **Summary:** subs back and waiting, out (and when the next is back) and in port; ventures done and retainers
    without one; characters below the low-supply warnings and the first to run out.
  - **Subs:** every built sub with its character, rank, build and a live countdown, grouped by character, with a
    filter.
  - **Supplies:** each character's ceruleum and repair kits, use per day, days left (amber under a week, red under three
    days) and how many to buy, as in the app.
  - **Status** is the old main window (connections, Check what I can see, the speed test); **Settings** the old
    settings window.
  - Click a column's header in Subs or Supplies to sort by it, again to reverse it (Status: soonest back first; rows
    with nothing to sort by, like a sub in port or a character without enough history, go last). Drag a column's edge
    to make it wider; the columns still stretch with the window, and the game remembers the widths.
  - The numbers come from the app's views in the database (app 1.8.0 or later), read in the background while the
    window is open: subs every 30 seconds, supplies and the summary every 5 minutes, or on **Refresh**.
- **Stage 4: what needs attention, and Fleet in the info bar.**
  - The Summary starts with **Needs attention**: subs back and waiting, then Free Company houses not entered for a
    while (red from 30 days), characters low on ceruleum or repair kits, characters no longer synced, and setups where
    AutoRetainer wasn't running. The same rules as the app's Summary. No pop-ups and nothing in chat.
  - **"Fleet" in the game's info bar** (top right, by the clock), always there so the window is one click away;
    "Fleet: 4 back · 1 low · 2 to check" when something needs you. Hover for the list, click to open the Summary. On by
    default; Settings → "Show Fleet in the game's info bar" turns it off. While it's shown it reads the subs and what
    needs attention every 30 seconds (a few milliseconds each), never the heavier supplies.
- **Stage 5: the sidebar, the looks and the new icon.**
  - The tabs make way for a **sidebar**: the submarine at the top, then the pages in three groups that fold
    (Fleet: Summary, Subs, Supplies; Tools: Check what I can see, Speed test; System: Status, Settings), each with an
    icon and, beside it, what needs attention (Subs · 4 back, Supplies · 1 low, Summary · how many things). The
    button at the top folds it down to its icons (hover one for its name and count). At its foot, when this PC last
    synced, or why it isn't syncing.
  - Check what I can see and the speed test are pages of the window now, not windows of their own; the credits are
    on the Status page.
  - **Looks** (Settings → Look): XIV Fleet Companion (the app's colors, the new default), Dalamud (like other
    plugins) or High contrast; and **colour-blind friendly status colors** (blue, yellow and orange-red in place of
    green, amber and red). Only this window changes.
  - The **new submarine icon**, for the plugin's picture and its icon in Dalamud's plugin list.
- **Stage 6: the rest of the tabs, part 1: Roster, Retainers and PCs.** Three more pages, sorted and filtered like
  Subs, read every minute while shown (app 1.8.0's views, from its test.8):
  - **Roster** (Fleet): every character with its Free Company, house, gil, ceruleum and repair kits (red below the
    low-supply warnings), retainers and subs, when it last synced and which setup syncs it; "not synced" beside one
    the plugin no longer syncs. The sidebar says how many aren't synced.
  - **Retainers** (Fleet): every retainer with its owner, class and level, gil, average item level, venture and a
    live countdown (Venture Complete in green, Idle in amber), and its gear target. The sidebar says how many ventures
    are done.
  - **PCs** (System): every setup of the plugin with its version (amber when older than another PC's), last sync,
    characters, which plugins it found and what it couldn't see (hover); then each setup's last "Check what I can
    see". The sidebar counts setups where AutoRetainer wasn't running.
  - Class, venture and item names and item levels come from the game's own files, so they're right on patch day.
  - These pages only show for now: reordering or removing characters, setting retainers' gear targets and removing a
    PC stay in the app.
  - `/xivfleet roster`, `/xivfleet retainers` and `/xivfleet pcs` open them.
- **Stage 6, part 2: Free Companies and Inventory, and the app's table controls everywhere.**
  - **Free Companies** (Fleet): every Free Company with its leader, world, points, members, house (or, without one,
    when it can bid) and how long since a member entered it (amber from the house warning, red from 30 days or with
    no date). The sidebar counts houses not entered for a while.
  - **Inventory** (Fleet), as the app's: **Characters** (what chosen characters and their retainers hold),
    **Free Companies** (chosen FC chests), **Fleet totals** (each item across the fleet, the most first) and
    **Where is it?** (type part of an item's name: every place the fleet holds it, in the Roster's order, by account).
    HQ items say so.
  - **Every table** (Subs, Supplies, Roster, Retainers, Free Companies, PCs, Inventory) works like the app's:
    click a heading to sort, again for the other way, a third time to go back; **Reset Sort** goes back at once;
    **Columns** shows, hides and reorders the columns (remembered; Reset columns undoes it); **Group by account**
    while not sorted. Long tables only draw the rows on screen.
  - **Retainers in the app's order**: by their owner's place in the Roster, then hiring order. The Roster and the Free
    Companies follow the Roster's saved order too.
  - **The Summary's retainer line matches the app's** and updates every 30 seconds instead of every 5 minutes, so a
    retainer caught between two ventures while AutoRetainer sends it out again no longer shows "without a venture" for
    minutes. Retainers of characters no longer synced are left out, as in the app.
  - `/xivfleet fcs` and `/xivfleet inventory` open the new pages.
- **Fleet in the info bar keeps working after an update.** Right after the plugin updated, clicking "Fleet" could do
  nothing until the plugin was turned off and on: the old copy's entry was still on screen. The plugin now asks again
  every few seconds until "Fleet" is its own, and takes it back if it ever isn't (noted in /xllog).
- **Stage 6, part 3: Salvage and Income** (with app 1.8.0's views). Salvage: the salvage each character holds, standard
  and extravagant, with each piece on hover and its value at the fixed salvage prices. Income: each character's real
  net income per day from its subs (salvage that came in, less repair kits used) and the venture rewards per character
  at what a vendor pays (from the game's files), over the Income window set in the app. Read every 5 minutes while
  shown. Fight Club's expected figures and Loot per Voyage come later in 2.x, with routes. `/xivfleet salvage` and
  `/xivfleet income` open them.
  Salvage leads with the Fleet-Wide Salvage Total, each hover ends with its total (pieces and gil), and the Summary
  shows the Fleet-Wide Salvage Total too, as the app's. The Summary also shows the venture income per day (last days of
  the Income window, at what a vendor pays), read with the supplies.
- **Tables that read at a glance**: colours that mean something (green when fine, amber getting low, red well below a
  warning: the Roster's ceruleum and repair kits against the app's warnings, Supplies' days left, Last synced, Income,
  houses, the PCs' plugins), subs as dots out of the slots unlocked, numbers on the right, large gil as "5.86M" (the
  full amount on hover), the world softer after each name, a band for each account with how many it has (and, on the
  Roster, which setup syncs it, so Synced by is hidden by default; Columns brings it back), a little more room per row
  and stronger stripes.
- **Gil in gold, house addresses in blue and Free Company names in lavender** (Roster, Retainers, Free Companies,
  Salvage). The warning colour is now orange rather than gold, so it's never mistaken for gil. With colour blind status
  colours these three stay plain, so the only colours there are the ones that mean something. The app uses the same
  colours.
- **Every column can be resized, the last one too**: columns keep the width they're given (fitted to their contents at
  first) instead of all stretching to fill the window, so the room left over sits after the last column, and a table
  wider than the window scrolls sideways. Column widths saved before this start over once.
- **Livelier Subs and Retainers.** Subs: each character is named once over its subs (while the table isn't sorted by a
  column), a thin bar beside the rank shows how far to the next, and the dashes in builds are softer. Retainers: each
  owner is named once, the class is in its role's colour (tanks blue, healers green, damage red, a base class such as
  Gladiator or Conjurer a lighter shade; gatherers plain) with the level softer, Avg ilvl is green when the gear meets
  the retainer's gear target and amber when it falls short (checked as the app does, from the game's files), and a
  venture's countdown is followed by the time it's done.
- FC points in aqua; ceruleum and repair kits that don't count toward the warnings (no sub built, or not synced) dim
  rather than plain; Income's tables name each character with its world, as everywhere else; and salvage totals (the
  Salvage page and the Summary) in gil's gold.
- **Stage 6 part 4: Parts**, as the app's Parts tab. **Detail**: every part a sub needs for the build it should have at
  its rank (its Sub Rules, or Fight Club's best build after repair kits for a sub ticked Craft? from rank 85), a new
  workshop's first sub and every unlocked empty slot, and where each comes from: a spare in bags, a retainer or the FC
  chest (a shared chest never promises one part twice), the part another sub gives up when upgraded, or crafting.
  **To craft**: how many of each part. The Summary shows the parts to craft and the subs ready for new parts with what
  each needs; the sidebar, the Summary's count and the info bar ("2 need parts") count them too. Worked out in the
  plugin from the app's views, the same way as the app (its tests write made-up fleets and the app's answers, which the
  plugin's tests check against). Sub Rules and Craft? are set in the app; Look ahead comes later in 2.x.
- **Stage 7: market prices.** A **Gil & Market** page, as the app's tab: the fleet's gil (characters and retainers)
  and gil per account, what's up for sale, and the listings unsold for a while (the app's days setting), oldest first.
  With **Live Market Prices** on in the app's Settings, the plugin asks Universalis itself (only item numbers and
  world, data center and region names are sent; answers kept 30 minutes): each listing's cheapest rival on its world,
  data center and region, the **undercut listings** (someone sells cheaper on the same world; biggest gap first, and
  a count beside the page in the sidebar), and venture rewards at market prices on the Income page and the Summary.
  With it off, nothing is asked. The Summary shows the listings unsold for a while, as the app's tile. The app's tests
  run its own Universalis code on canned listings, and the plugin's tests check it gets the same answers.
- Free Companies name each FC with its world after it, softer, instead of a separate World column (its members are
  all on it); a sub's rank sits right after its bar, both on the left, so stretching the column doesn't pull them apart.
- **Searching word by word** in every Filter box and Where is it?: every word typed, in any order ("diadochos healing"
  finds every piece of Diadochos healing gear).
- **Hide equipped gear** on the Inventory page's Characters, Fleet totals and Where is it?: leaves out gear being worn by
  the characters and their retainers. One choice for all three, saved.
- **Free Companies and Inventory read with real Free Company ids.** These ids are too big for the number the plugin
  read them into, so the Free Companies list failed and took every other read down with it ("Could not reach the
  database" everywhere until the next read, and again on every Refresh). They're read in full now, and a read that
  fails for any reason other than the connection now fails alone, saying why, keeping what it read before.
- **Where is it?'s columns line up.** Every item's table now has the same column widths (from the widest text shown),
  instead of each sizing to its own rows.

## 1.0.3 - 2026-11-05

Goes with app 1.7.0, which removes the Discord alerts.

- The settings no longer mention the Discord alerts (the tooltip of "Also sync right after a character logs out"), and
  neither does the download page.

## 1.0.2 - 2026-11-05

Goes with app 1.6.0, which adds the table this uses to the database by itself. With an older app, the plugin syncs
as before.

- **Only the setup a character last logged in on syncs it.** Each setup (PC and account label) notes in the database
  when a character logs in on it. A setup then leaves alone any character that last logged in on another setup, so
  an old copy of that character left in its AutoRetainer can't overwrite the real one any more. Nobody has to tidy
  AutoRetainer for this. A character that hasn't logged in anywhere since the update keeps syncing as before, and
  there's no time limit: after a long break, each character still belongs to the setup it last logged in on.
  Changing a setup's account label takes its characters along. A character unticked in a setup's settings never counts
  as that setup's, and unticking one gives up the claim, so unticking a character on one PC (say, a laptop) never stops
  another PC that syncs it. The plugin's window says how many characters it is leaving to another setup.

- **Test builds.** New versions of the plugin can now be tried before everyone else gets them: players who opt in to
  the plugin's testing builds in Dalamud get them, and everyone else stays on the normal version. Each normal release
  is numbered above the last test build, so testers get it too. The download page says how to opt in.
- **The app's PCs tab says how many characters each setup leaves to another one** (with app 1.6.0).
- **A reminder to set an account label.** While a setup's Account Label is blank, the plugin's window and its
  settings say to give it one (for example, the account's name). Without one, the app can't tell your accounts
  apart, setups on one PC share a row on its PCs tab, and it can't spot two setups syncing the same character.
  App 1.6.0 points out characters and setups without a label too.

## 1.0.1 - 2026-10-19

No database change. Works with any app version.

- **Check what I can see says where the items are.** The line that said "Bags: 153 items" counted every filled slot
  Allagan Tools has for the character, not just the bags: crystals, saddlebags, the armoury chest, what they are
  wearing and so on. It is now "Character: 153 items (bags 17, crystals 18, saddlebags 40, armoury 70, equipped 8)",
  so each number can be matched to what the game shows. Retainers get the same, like "(bags 22, for sale 3)". A count
  that is all in one place has no breakdown.

## 1.0.0 - 2026-10-19

The first stable release. Nothing changed since 0.14.2; works with app 0.36.0 or newer, and with app 1.0.0.

## 0.14.2 - 2026-10-19

No database change. Works with any app version.

- **An easier-to-read description** in Dalamud's plugin list, saying what the plugin needs, what helps, and that it
  works with the XIV Fleet Companion app. The author is now shown as CrownedPandaXIV.
- **"Database" instead of "Postgres"** in the plugin's windows, as in the app and the guides: **Database Connection**,
  **Save Database Credentials** and **Test Database Connection**.
- **A quieter log:** "Allagan Tools is not available" is written once, not at every sync (and again if it goes away
  after coming back), and the FC Tracker line at every sync is now only in the detailed log.

## 0.14.1 - 2026-10-19

No database change. Works with any app version.

- **The main window says when syncing isn't getting through,** under "Last sync", in plain words: for example "Can't
  reach the database. Check the PC with the database is on, and that Tailscale is connected if you use it." It goes
  as soon as a sync gets through again.
- **A quieter log:** a problem like that is written to the log once, not with a long error every sync, and the log
  says when syncing works again.
- Removed the **Read My Character Data** and **Read My Inventory** test buttons from the main window. **Check what I
  can see** shows the same and more.

## 0.14.0 - 2026-10-19

No database change needed. Works with any app version; moving the database needs app 0.35.0 or newer on the PC it
moves to.

- Follows the database when it moves to another PC (the app's Settings → Database → **Move the database to this PC**).
  At its next sync it finds the new address the app left in the old database, connects to it with the same database
  name, username and password, and saves it in its settings. If this PC can't reach the new address, the log says
  so once and nothing is synced until it can; it tries again at every sync.
- While the app is copying the database, the plugin waits instead of syncing, and tries again every minute. If the
  app is closed mid-move, the plugin carries on as usual after 5 minutes.
- Each PC's status now includes the database login the plugin uses, so a move can check every PC will be let in.
- The plugin's database connections are named "XIV Fleet Companion plugin", so the app can tell when a sync is still
  running.

## 0.13.6 - 2026-10-18

No database change. Works with any app version.

- The main window's Connections list now shows FC Tracker and Submarine Tracker too ("running" or "not running
  (optional)"), so all four plugins it works with are in one place.

## 0.13.5 - 2026-10-18

No database change. Works with any app version.

- `/xivfleet check` now says whether Submarine Tracker is running, installed but not running, or not found, alongside
  AutoRetainer, Allagan Tools and FC Tracker. It's optional, so it's never listed as something to fix.

## 0.13.4 - 2026-10-17

No database change. Works with any app version.

- The plugin's description in Dalamud's plugin list now says it needs AutoRetainer and works best with Allagan Tools,
  with the repository addresses for AutoRetainer and FC Tracker, and thanks the people who make them.
- The README lists the repository to add for each plugin it works with, and has a Thanks section: kawaii,
  NightmareXIV and the Puni.sh team (AutoRetainer), Critical_Impact (Allagan Tools), erdelf (FC Tracker) and Infi
  (Submarine Tracker).
- The plugin's windows and messages now write the plugins' names the way their makers do: Allagan Tools, FC Tracker,
  Submarine Tracker.

## 0.13.3 - 2026-10-17

No database change. Works with any app version.

- The main window's status says "running" and "not running", the same words as `/xivfleet check`, instead of
  "OK" and "Not found / not running". "Postgres: Configured" reads "Database: set up".

## 0.13.2 - 2026-10-17

No database change. Works with any app version.

- **FCTracker, read in one go.** With the newest FCTracker, the plugin asks for every character's and Free
  Company's details in two calls, instead of one call per character and Free Company. Older FCTrackers are asked
  one at a time as before, and the oldest are still read from their file.
- `/xivfleet check` shows FCTracker's IPC version when it says, for example "details read directly from it
  (IPC version 1)".

## 0.13.1 - 2026-10-17

No database change. Nothing changes in the plugin itself.

- The changelog in the game's plugin list (`/xlplugins`) is now plain text: no stray `**` marks, and no
  lines broken in the middle of a sentence.

## 0.13.0 - 2026-10-17

No database change. Works with any app version.

- **FCTracker, asked directly.** When FCTracker is running and has its new IPC (added by its developer, erdelf,
  in October 2026), the plugin asks it for each character's Free Company and house details instead of reading
  FCTracker's config file. Nothing to set up. An FCTracker without the IPC, or one that is not running, is read
  from its file as before.
- **Check what I can see** says which was used: "details read directly from it", or "a version without IPC: its
  saved file is read".

## 0.12.1 - 2026-10-17

No database change. Works with any app version; app 0.26.0 or newer sets up and updates the database.

- **No more SQL to run by hand.** When the database is missing a table or column, the plugin's log and its
  "Check what I can see" window now say to open the app once (it makes the change itself), instead of naming a
  SQL file to run.
- **Friendlier instructions:** the README and the download page are rewritten for players first, with the
  developer notes at the end. The download page (XIVFleetCompanion-Releases) now stays in step with this
  repository by itself.

## 0.12.0 - 2026-10-09

No database change. For app 0.23.2.

- **FCTracker and SubmarineTracker: running, not just installed.** The app's PCs tab now shows whether each is
  loaded in that game, for that account (from Dalamud's list of loaded plugins), like AutoRetainer and
  AllaganTools. Before, it only showed whether their files existed, which stays true when they are turned
  off, and on a PC running two accounts one account's files counted for both.
- With subs and SubmarineTracker installed but not running: "SubmarineTracker is not running: new voyages'
  loot is not recorded." Voyages already in its file are still read.
- **Check what I can see** says whether FCTracker is running, installed but not running (its last saved
  details are still used), or not found.
- If Dalamud cannot be asked, the files decide, as before.

## 0.11.1 - 2026-10-07

No database change.

- **A database error no longer cuts a sync short.** If saving this PC's status, voyage loot or venture
  rewards fails, it is logged and the rest of the sync (the history for Trends, the last sync time) still
  saves. Before, the sync stopped there.
- **"Open the Free Company chest" is listed once per FC** on the app's PCs tab, not once for every
  character in it.
- Tidying, with no change in what the plugin does: shared code for finding other plugins' folders, for
  asking again for the sql/008 tables, and for counting items in **Check what I can see**.

## 0.11.0 - 2026-10-07

No database change. For app 0.21.0.

- **AutoRetainer status.** When AutoRetainer is not running, nothing can be synced, but the app's PCs tab
  is now told so (from the second sync attempt in a row, so AutoRetainer still loading as the game starts
  is not reported). The PC keeps its last sync's time and counts.
- **Only what is used is asked for.** For players who run only retainers or only subs:
  - The Free Company chest is only asked for when the FC has subs (it holds their ceruleum, repair kits and
    salvage). A chest that was opened is still read.
  - SubmarineTracker missing is only reported when a synced character has subs.
  - FCTracker is optional (only Free Company house details): not finding it is shown, but no longer listed as
    something to fix, here or in **Check what I can see**.

## 0.10.1 - 2026-10-07

Optional database change: `sql/009_voyage_sector_order.sql` (adds one column; safe to run more than once).
Everything works as before until it has been run. For app 0.20.2.

- **Route order.** Each voyage's sectors are stored with the order they were run in (the order
  SubmarineTracker recorded them), so the app shows routes as they were run instead of by sector number.
  The first sync after sql/009 fills in the order of the voyages already stored.

## 0.10.0 - 2026-10-07

Optional database change: `sql/008_pcs_loot_ventures_listings.sql` (adds four tables; safe to run more
than once). Everything works as before until it has been run. For app 0.20.0.

- **PCs.** Each sync stores this PC's status: plugin version, last sync, which of AutoRetainer,
  AllaganTools, FCTracker, SubmarineTracker and AutoRetainer's venture statistics it found, and what it
  could not see. **Check what I can see** also stores its result. The app shows every PC in one place.
- **Loot per voyage.** The loot SubmarineTracker records for each voyage is copied to the database
  (read only from its file; only new voyages), for exact income per sub and route in the app.
- **Venture rewards.** The rewards AutoRetainer records for each venture are copied to the database
  (only new ones), for the app's venture income.
- **Market listings.** What each retainer has up for sale (from AllaganTools), at what price and since
  when, for the app's listings that have sat unsold.
- Uses the SQLite engine Dalamud already loads (the same package SubmarineTracker uses) to read
  SubmarineTracker's file.

## 0.9.1 - 2026-10-07

Optional database change: `sql/007_retainer_items_seen.sql` (adds one column; safe to run more than
once). Everything works as before until it has been run. For app 0.19.0.

- **When each retainer was last seen.** With sql/007, the plugin records when AllaganTools saw each
  retainer's items, even when it holds nothing, so the app no longer lists an empty retainer as unseen.
- **Supplies across bags and retainers.** Ceruleum tanks and Magitek repair materials held in bags and
  retainers are recorded (`item_qty:10155`, `item_qty:10373`), so the app can tell supplies used on
  voyages from supplies moved to a retainer.
- **First-time items are marked.** `inventory_sources` records how many sources have items stored; it
  goes up when a retainer's items are stored for the first time (a new PC, a new retainer), so the app
  does not count those items as income.

## 0.9.0 - 2026-10-07

No database changes. Works with any app version; app 0.18.0 lists the retainers whose items have not
been seen.

- **A retainer AllaganTools has not seen no longer loses its stored items.** AllaganTools answers with
  nothing at all for a retainer it has never seen on this PC (not opened at a summoning bell since it was
  installed or hired), and the plugin used to store that as an empty retainer, deleting whatever was
  stored for it. Items are now stored per source: the bags and each retainer AllaganTools has seen are
  replaced, and an unseen retainer keeps its last stored items. A dismissed retainer's items still go.
  The same for the bags of a character not yet seen on this PC, and for an FC chest not opened on this
  PC (it used to be cleared).
- **The log says what to open.** For example: "AllaganTools has not seen retainer Newret's items (Aki
  Main@Maduin); open Newret at a summoning bell on this PC." Once per retainer per game session, not every
  sync.
- **Salvage counts** for the salvage and income charts are now worked out from what is stored after the
  upload, so they include an unseen retainer's last stored salvage instead of counting it as zero.
- **Check what I can see** (button in the main window, or `/xivfleet check`): goes through every
  character this plugin syncs and shows what AutoRetainer, AllaganTools (bags, each retainer, the FC
  chest) and FCTracker (the house) have for it, when each was last stored in the database, and what to
  open or log into to fix anything missing. It only reads; nothing is written. **Copy results** copies it
  to share.

## 0.8.1 - 2026-10-05

No database changes.

- **House addresses were one ward and one plot too low.** FCTracker saves the game's own 0-based
  numbers (Ward 7 Plot 28 is saved as 6 and 27) and adds 1 only when it shows them; the plugin copied
  them as saved. It now stores the real address, so the app's Roster, Free Companies tab and house
  warning show it correctly. Each plugin rewrites housing within the hour after updating, which corrects
  the stored addresses.

## 0.8.0 - 2026-10-05

No database changes.

- **Choose which characters are synced.** A Characters section in settings lists every character
  AutoRetainer knows on this client, each with a tick box. Either sync every character except the ones
  unticked (the default, so nothing changes for existing setups and new characters are synced), or only
  the ones ticked (for an account with many characters of which only a few should reach the app; new
  characters are then left out until ticked). Each way keeps its own list. Buttons tick only the
  characters with subs, all, or none. Left-out characters are not read or sent at all, and the sync log
  says how many were left out.
- The choice is in its own small class (CharacterChoice.cs), checked by tests/FleetWriterTests.

## 0.7.0 - 2026-10-04

No database changes.

- **Sub rank and experience history.** Each sub's rank and experience are recorded in the metric history
  (as `sub_rank:N` and `sub_exp:N` for workshop slot N, labelled with the sub's name), only when they
  change plus the hourly reminder, like gil and ceruleum. App 0.17.0 uses them for each sub's rank-up
  date.

## 0.6.0 - 2026-10-03

No database changes.

- **Instant sync after AutoRetainer finishes a character.** AutoRetainer logs a character out once it
  has collected and resent its subs and retainers; the plugin now syncs about 5 seconds after any
  logout instead of waiting up to the sync interval, so the app and the Discord alerts (app 0.11.0) see
  that character's subs straight away. At most one sync every 30 seconds, so quick relogs do not start
  one each; a logout during a sync gets its own sync once that one finishes. The regular interval still
  runs as before. "Also sync right after a character logs out" in settings (on by default) turns it off.
- The sync timing is in its own small class (SyncSchedule.cs), checked by tests/FleetWriterTests.

## 0.5.1 - 2026-10-02

No database changes.

- **The settings form no longer wipes a saved connection.** It now opens filled in with the saved host,
  port, database and username; leaving the password box empty keeps the saved password. Saving with an
  empty host, database or username is refused. "Clear Saved Credentials" needs a second click.
- The main window checks Postgres, AutoRetainer and AllaganTools every few seconds instead of on every
  frame, so an unreadable saved credential no longer writes a warning to the log many times a second.
- AutoRetainer's data is copied on the game's thread at the start of each sync, so a sync no longer
  reads it while AutoRetainer is changing it (which could fail that sync).
- Tidy-ups: the main window no longer shows "Enabled" and "Last sync" twice; the unused "Movable Config
  Window" option and leftover template comments are gone. The windows have new internal IDs, so they
  open once at their default position and size.

## 0.5.0 - 2026-10-02

Run `sql/006_submarine_slot.sql` once (before or after updating; instant, safe to run again). Without
it the plugin keeps working as before, just without slots.

- **Renamed subs are handled.** Each sub is stored with its workshop slot (1-4), taken from its place in
  AutoRetainer's list of the character's subs, so the app (0.8.0) can find a slot's sub whatever it is
  called. Before, a sub renamed from `Submersible-N` was treated by the app's Parts tab as an empty slot.
- **No leftover subs after a rename.** Only subs in AutoRetainer's current list are written; build data
  AutoRetainer still holds under an old name is ignored.
- **The Craft? setting follows a rename.** When a slot's sub gets a new name (and the old name is gone),
  its Craft? setting in the app moves to the new name instead of being lost. Two subs swapping names keep
  their own settings.

## 0.4.2 - 2026-10-02

- Fixed: the main window's **Read My Character Data** test showed the world the character was currently
  on (AutoRetainer's override, for example during data center travel) instead of its home world, which
  is what the sync, the database and the app use. It now shows the home world, and adds "(currently on
  ...)" when the character is visiting another world. Nothing that is synced was affected.

## 0.4.1 - 2026-10-02

- **Fixed: Browse... froze the game.** The FCTracker config path's Browse button opened the Windows file
  picker from inside the game's drawing loop, so the game stopped (no frames, no input, AutoRetainer
  paused) until it was closed, and in fullscreen the picker could open behind the game. It now uses
  Dalamud's own file picker, drawn inside the game, which keeps running.
- **Found / not found** under the path shows whether the file is there (checked when the path changes
  and every few seconds), so a wrong path is visible without waiting for a sync.
- **Use default** puts back the standard path (`pluginConfigs\FCTracker\FCTrackerConfig.json`, the one
  filled in on first load); hover it to see the path.
- The plugin no longer uses Windows Forms.

## 0.4.0 - 2026-10-02

Needs `sql/004_character_current.sql` to have been run (from 0.3.0). Characters are saved only in
`companion_character_current` from now on.

- **The old snapshot history is no longer written.** `companion_character_snapshot` got a full row per
  character on every sync; the current values, the chart history and the change log now hold
  everything it was used for. Saving the old table to a file and removing it is up to you (see the
  README: the app's `backup\save-character-snapshot.ps1`, then `sql/005_remove_character_snapshot.sql`).
- **Retention & Downsampling is gone** from the settings, with its daily clean-up: it only ever
  thinned out that table.
- New `sql/005_remove_character_snapshot.sql`: removes the old table, and refuses (changing nothing)
  while a plugin older than 0.4.0 still writes it or a character has no current row.

## 0.3.1 - 2026-10-02

No database script to run.

- **Dismissed retainers are removed.** When a character's retainers are written, its retainers that are
  no longer in AutoRetainer's list are deleted, so they drop out of the app's Retainers tab and gil
  totals. An empty list is ignored (more likely missing data than every retainer dismissed).
- **Chests of Free Companies you are no longer in are removed.** After each sync, the chest of any Free
  Company that none of your tracked characters (on any account) belongs to is deleted, so it no longer
  counts in the fleet inventory and salvage totals.
- **Old Free Company and house details are removed** from a character that has left its Free Company,
  once FCTracker and AutoRetainer both say it is in none. A Free Company losing its house was already
  handled: the house is cleared as soon as FCTracker sees it gone.

## 0.3.0 - 2026-10-02

Needs `sql/004_character_current.sql` to be run once first (it is safe to run more than once).

- **Current state per character.** Each sync updates one row per character in the new
  `companion_character_current` table (name, world, account, gil, ceruleum, repair kits, retainers,
  submarines, sub slots, Free Company, first seen, last synced), in one statement. The app reads this
  instead of searching the snapshot history for each character's newest row.
- **Identity changes are kept.** A change of name, world, account label or Free Company is logged in
  `companion_character_changes` (by the database, whenever the current row changes).
- **More chart history.** Retainer count, submarine count and sub slots are recorded in the metric
  history when they change, like gil and ceruleum.
- `sql/004` copies the existing history into these: each character's newest snapshot, every past
  change of retainers, submarines and sub slots, and every past name, world, account and Free Company
  change.
- The snapshot table is still written every sync for now, as a safety net. Until `sql/004` has been
  run, the plugin skips the current table with one warning per session and works as before.

## 0.2.0 - 2026-10-02

- **Much less database writing.** Inventories, FC chests, submarines, retainers and housing are
  now written only when they changed since the plugin last wrote them, instead of every sync.
  Everything is still rewritten once an hour (and on the first sync after the plugin loads), so the
  database catches up if it was changed or restored from elsewhere. Character snapshots and the
  chart history are written every sync as before.
- **Fewer round trips.** A sync uses one database connection, and each table is written in one
  statement per character (every character's snapshot in a single statement) instead of one
  statement per item. Before, a sync sent about 15,000 separate inventory inserts.
- The plugin log line now says how many entries were written, skipped as unchanged, or failed.
- "Last sync" in the plugin window only updates when the characters were actually written.
- A password containing `;` or `=` no longer breaks the connection settings.
- New automatic tests run the database writes against a scratch Postgres
  (`tests/FleetWriterTests`).

## 0.1.2 - 2026-10-02

- Inventories are no longer emptied when AllaganTools is not running. Before, a sync while
  AllaganTools was disabled (for example while it waits for an update after a patch) replaced every
  character's stored inventory and FC chest with nothing, so the app's Parts, Salvage and Inventory
  tabs showed empty until it came back. Now the stored contents are kept and the plugin log says
  that inventories were not updated. The same applies to a single character AllaganTools has no
  data for yet.
- Retainer details (name, class, level, gil, venture) come from AutoRetainer and are now written
  even when AllaganTools is not running. Before, they also stopped updating.

## 0.1.1 - 2026-09-29

- Much faster cleanup of old snapshot history. The old cleanup query could run for many minutes
  (or effectively never finish) on a table of millions of rows; the new one does a 4-million-row
  cleanup in about 20 seconds, keeping exactly the same rows. The cleanup also now has a 10-minute
  time limit instead of 30 seconds.
- New recommended index for the daily cleanup: `sql/003_snapshot_timestamp_index.sql`.
- The default retention window is now 2 months (was 6). Existing installs keep whatever they have
  saved; change it in the settings window. Long-term history is kept in the metric history table.

## 0.1.0 - 2026-09-29

First tracked version. Everything the plugin does so far:

- Syncs characters, retainers, submarines, inventory, Free Company chests and housing data
  (from AutoRetainer, AllaganTools and FCTracker) into Postgres on a configurable interval.
- Postgres credentials kept in Windows Credential Manager, with separate local and remote
  entries; an unreadable credential is now logged instead of failing silently.
- Daily retention and downsampling of the character snapshot history.
- Metric history for charts: gil, ceruleum, repair kits, FC points (associated with the FC
  leader) and salvage item quantities, stored only when they change
  (`sql/001_metric_history.sql`, optional backfill in `sql/002_...`).
- The version is shown in the main and settings window titles, in the plugin installer, and in
  the log on load.
