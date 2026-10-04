# Changelog

All notable changes to the plugin are recorded here. Versions follow
[Semantic Versioning](https://semver.org): MAJOR.MINOR.PATCH.

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
