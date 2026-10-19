-- Remembers the order each voyage ran its sectors in (plugin 0.10.1): leg 1 is the first sector visited,
-- so the app shows routes in the order they were run instead of by sector number.
-- Run once, before or after updating the plugin (safe to run more than once; instant; changes no data).
-- Until it has been run, the plugin and the app work as before. Afterwards, the next sync on each PC fills
-- in the order of the voyages already stored (from SubmarineTracker's file).

ALTER TABLE companion_voyage_loot ADD COLUMN IF NOT EXISTS leg int;
