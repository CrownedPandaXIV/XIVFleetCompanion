# Changelog

All notable changes to the plugin are recorded here. Versions follow
[Semantic Versioning](https://semver.org): MAJOR.MINOR.PATCH.

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
