# Changelog

All notable changes to the plugin are recorded here. Versions follow
[Semantic Versioning](https://semver.org): MAJOR.MINOR.PATCH.

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
