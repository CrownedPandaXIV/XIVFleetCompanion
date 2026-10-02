-- Only for databases that still have companion_character_snapshot (plugin 0.3.x and older).
-- OPTIONAL, run once, after 001. Copies the gil / ceruleum / repair kit history that
-- already exists in companion_character_snapshot into companion_metric_history, keeping
-- only the moments a value changed, so charts of those three start with real history.
-- Safe to run before or after the plugin starts writing metrics. On a multi-million-row
-- snapshot table this can take a minute or two.

INSERT INTO companion_metric_history (recorded_at, subject_type, subject_id, metric, value)
SELECT snapshot_at, 'character', cid, metric, value
FROM (
    SELECT s.cid, s.snapshot_at, v.metric, v.value,
           lag(v.value) OVER (PARTITION BY s.cid, v.metric ORDER BY s.snapshot_at) AS previous_value
    FROM companion_character_snapshot s
    CROSS JOIN LATERAL (VALUES
        ('gil',         s.gil::numeric),
        ('ceruleum',    s.ceruleum::numeric),
        ('repair_kits', s.repair_kits::numeric)
    ) AS v(metric, value)
    WHERE s.gil IS NOT NULL AND s.ceruleum IS NOT NULL AND s.repair_kits IS NOT NULL
) changes
WHERE previous_value IS DISTINCT FROM value;

-- Seed "latest" from the newest snapshot so the plugin does not re-record unchanged values.
INSERT INTO companion_metric_latest (subject_type, subject_id, metric, value, recorded_at)
SELECT DISTINCT ON (subject_id, metric) subject_type, subject_id, metric, value, recorded_at
FROM companion_metric_history
WHERE subject_type = 'character' AND metric IN ('gil', 'ceruleum', 'repair_kits')
ORDER BY subject_id, metric, recorded_at DESC
ON CONFLICT (subject_type, subject_id, metric) DO NOTHING;
