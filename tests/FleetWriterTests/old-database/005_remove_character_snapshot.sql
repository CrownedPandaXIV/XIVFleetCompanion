-- Removes the old per-sync history table, companion_character_snapshot (Phase B). Plugin 0.4.0 no
-- longer writes it: every character's current values are in companion_character_current (sql/004),
-- and the history worth keeping is in companion_metric_history (values over time, for Trends) and
-- companion_character_changes (name, world, account and Free Company changes).
--
-- Order:
--   1. Update the plugin to 0.4.0 on every PC that runs it, and let it sync once.
--   2. Save the table to a file and check the file: the app's backup\save-character-snapshot.ps1.
--   3. Run this script. Safe to run more than once.
--
-- It refuses (and changes nothing) when:
--   - companion_character_current does not exist (run sql/004 first);
--   - a character in the old table has no current row (it would be lost from the app);
--   - the old table was written in the last 10 minutes (a plugin older than 0.4.0 is still running).
-- The indexes on the table (sql/003 and the app's README) go with it.

DO $$
DECLARE
    newest timestamptz;
    missing bigint;
BEGIN
    IF to_regclass('companion_character_snapshot') IS NULL THEN
        RAISE NOTICE 'companion_character_snapshot was already removed; nothing to do.';
        RETURN;
    END IF;
    IF to_regclass('companion_character_current') IS NULL THEN
        RAISE EXCEPTION 'companion_character_current does not exist. Run sql/004_character_current.sql first. Nothing was changed.';
    END IF;

    SELECT count(*) INTO missing
    FROM (SELECT DISTINCT cid FROM companion_character_snapshot) s
    WHERE NOT EXISTS (SELECT 1 FROM companion_character_current c WHERE c.cid = s.cid);
    IF missing > 0 THEN
        RAISE EXCEPTION '% character(s) in companion_character_snapshot have no row in companion_character_current. Run sql/004_character_current.sql again. Nothing was changed.', missing;
    END IF;

    SELECT max(snapshot_at) INTO newest FROM companion_character_snapshot;
    IF newest > now() - interval '10 minutes' THEN
        RAISE EXCEPTION 'companion_character_snapshot was written % ago, so a plugin older than 0.4.0 is still running. Update it everywhere, wait 10 minutes, then run this again. Nothing was changed.',
            date_trunc('second', now() - newest);
    END IF;

    DROP TABLE companion_character_snapshot;
    RAISE NOTICE 'companion_character_snapshot removed.';
END $$;
