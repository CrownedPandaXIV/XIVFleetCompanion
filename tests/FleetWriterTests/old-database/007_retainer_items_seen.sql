-- Remembers when each retainer's items were last seen by AllaganTools (plugin 0.9.1), so the app can
-- tell a retainer that holds nothing from one that has never been opened at a summoning bell.
-- Run once, before or after updating the plugin (safe to run more than once; instant; changes no data).
-- Until it has been run, the plugin and the app work as before.

ALTER TABLE companion_retainer_lookup ADD COLUMN IF NOT EXISTS items_seen_at timestamptz;
