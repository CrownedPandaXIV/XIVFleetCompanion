-- Recommended, run once (safe to run more than once). Speeds up the plugin's daily cleanup of
-- old snapshot history: with it, a normal day's cleanup took about 0.5 s instead of 3 s on a
-- 10-million-row table. It also lets Postgres jump straight to the old rows. About 95 MB per
-- 14 million rows.

CREATE INDEX IF NOT EXISTS companion_character_snapshot_snapshot_at_idx
    ON companion_character_snapshot (snapshot_at);

ANALYZE companion_character_snapshot;
