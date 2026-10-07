# Changelog

All notable changes to the plugin are recorded here. Versions follow
[Semantic Versioning](https://semver.org): MAJOR.MINOR.PATCH.

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
