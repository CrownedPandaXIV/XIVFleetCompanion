-- Current state per character, plus the history worth keeping, instead of one full snapshot row
-- per character per sync. Run this once, after 001 (safe to run more than once). It takes a minute
-- or two on a few million snapshot rows. Run it BEFORE updating the plugin to 0.3.0.
--
-- companion_character_current   one row per character, updated by the plugin every sync: what the
--                                app shows (name, world, account, gil, ceruleum, repair kits,
--                                retainers, submarines, sub slots, Free Company), when the character
--                                was first seen and when it last synced.
-- companion_character_changes   one row each time a character's name, world, account label or Free
--                                Company changes (filled automatically when the current row changes).
-- companion_metric_history      gains retainer_count, submarine_count and num_sub_slots (written by
--                                the plugin from 0.3.0 on, only when they change).
--
-- From the existing companion_character_snapshot table this script copies:
--   - the newest row per character into companion_character_current (first seen = oldest row);
--   - every change of retainer count, submarine count and sub slots into the metric history
--     (for characters that do not have those metrics yet);
--   - every change of name, world, account label and Free Company into companion_character_changes
--     (only when that table is still empty, so a second run adds nothing).
-- The snapshot table itself is not changed.

BEGIN;

CREATE TABLE IF NOT EXISTS companion_character_current (
    cid             numeric     PRIMARY KEY,
    name            text,
    world           text,
    account_label   text,
    gil             bigint,
    ceruleum        int,
    repair_kits     int,
    retainer_count  int,
    submarine_count int,
    num_sub_slots   int,
    fc_id           numeric,
    first_seen_at   timestamptz NOT NULL DEFAULT now(),
    last_synced_at  timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX IF NOT EXISTS companion_character_current_fc_id_idx
    ON companion_character_current (fc_id);

CREATE TABLE IF NOT EXISTS companion_character_changes (
    changed_at timestamptz NOT NULL DEFAULT now(),
    cid        numeric     NOT NULL,
    field      text        NOT NULL,   -- name, world, account_label or fc_id
    old_value  text,
    new_value  text
);

CREATE INDEX IF NOT EXISTS companion_character_changes_cid_idx
    ON companion_character_changes (cid, changed_at DESC);

-- Records which of the tracked fields changed when a current row is updated.
CREATE OR REPLACE FUNCTION companion_character_record_changes() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO companion_character_changes (changed_at, cid, field, old_value, new_value)
    SELECT now(), NEW.cid, f.field, f.old_value, f.new_value
    FROM (VALUES ('name', OLD.name, NEW.name),
                 ('world', OLD.world, NEW.world),
                 ('account_label', OLD.account_label, NEW.account_label),
                 ('fc_id', OLD.fc_id::text, NEW.fc_id::text)) AS f(field, old_value, new_value)
    WHERE f.old_value IS DISTINCT FROM f.new_value;
    RETURN NULL;
END $$;

DROP TRIGGER IF EXISTS companion_character_current_changes ON companion_character_current;
CREATE TRIGGER companion_character_current_changes
    AFTER UPDATE ON companion_character_current
    FOR EACH ROW
    WHEN (OLD.name IS DISTINCT FROM NEW.name
       OR OLD.world IS DISTINCT FROM NEW.world
       OR OLD.account_label IS DISTINCT FROM NEW.account_label
       OR OLD.fc_id IS DISTINCT FROM NEW.fc_id)
    EXECUTE FUNCTION companion_character_record_changes();

-- Copy what is worth keeping from the snapshot history (skipped when that table does not exist).
DO $$
BEGIN
    IF to_regclass('companion_character_snapshot') IS NULL THEN
        RETURN;
    END IF;

    INSERT INTO companion_character_current
        (cid, name, world, account_label, gil, ceruleum, repair_kits, retainer_count, submarine_count,
         num_sub_slots, fc_id, first_seen_at, last_synced_at)
    SELECT l.cid, l.name, l.world, l.account_label, l.gil, l.ceruleum, l.repair_kits, l.retainer_count,
           l.submarine_count, l.num_sub_slots, l.fc_id, f.first_seen, l.snapshot_at
    FROM (SELECT DISTINCT ON (cid) * FROM companion_character_snapshot ORDER BY cid, snapshot_at DESC) l
    JOIN (SELECT cid, min(snapshot_at) AS first_seen FROM companion_character_snapshot GROUP BY cid) f USING (cid)
    ON CONFLICT (cid) DO NOTHING;

    INSERT INTO companion_metric_history (recorded_at, subject_type, subject_id, metric, value)
    SELECT c.snapshot_at, 'character', c.cid, c.metric, c.value
    FROM (
        SELECT s.cid, s.snapshot_at, v.metric, v.value,
               lag(v.value) OVER (PARTITION BY s.cid, v.metric ORDER BY s.snapshot_at) AS previous_value
        FROM companion_character_snapshot s
        CROSS JOIN LATERAL (VALUES
            ('retainer_count',  s.retainer_count::numeric),
            ('submarine_count', s.submarine_count::numeric),
            ('num_sub_slots',   s.num_sub_slots::numeric)
        ) AS v(metric, value)
        WHERE v.value IS NOT NULL
    ) c
    WHERE c.previous_value IS DISTINCT FROM c.value
      AND NOT EXISTS (SELECT 1 FROM companion_metric_latest l
                      WHERE l.subject_type = 'character' AND l.subject_id = c.cid AND l.metric = c.metric);

    INSERT INTO companion_metric_latest (subject_type, subject_id, metric, value, recorded_at)
    SELECT DISTINCT ON (subject_id, metric) subject_type, subject_id, metric, value, recorded_at
    FROM companion_metric_history
    WHERE subject_type = 'character' AND metric IN ('retainer_count', 'submarine_count', 'num_sub_slots')
    ORDER BY subject_id, metric, recorded_at DESC
    ON CONFLICT (subject_type, subject_id, metric) DO NOTHING;

    IF NOT EXISTS (SELECT 1 FROM companion_character_changes) THEN
        INSERT INTO companion_character_changes (changed_at, cid, field, old_value, new_value)
        SELECT c.snapshot_at, c.cid, c.field, c.previous_value, c.value
        FROM (
            SELECT s.cid, s.snapshot_at, v.field, v.value,
                   lag(v.value) OVER (PARTITION BY s.cid, v.field ORDER BY s.snapshot_at) AS previous_value,
                   row_number() OVER (PARTITION BY s.cid, v.field ORDER BY s.snapshot_at) AS n
            FROM companion_character_snapshot s
            CROSS JOIN LATERAL (VALUES
                ('name', s.name),
                ('world', s.world),
                ('account_label', s.account_label),
                ('fc_id', s.fc_id::text)
            ) AS v(field, value)
        ) c
        WHERE c.n > 1 AND c.previous_value IS DISTINCT FROM c.value;
    END IF;
END $$;

COMMIT;

ANALYZE companion_character_current;
ANALYZE companion_character_changes;
