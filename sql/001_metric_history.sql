-- Metric history: a general-purpose place for values you want to chart over time.
-- Run this once in the database the plugin writes to (safe to run more than once).
--
-- companion_metric_history  one row each time a value CHANGED (plus an hourly
--                           "still the same" row), so slow-moving values stay small.
-- companion_metric_latest   the last value seen per subject and metric; the plugin
--                           uses it to decide whether a new history row is needed.
--
-- subject_type / subject_id say what the value belongs to:
--   'character' + character id      e.g. metric 'gil', 'ceruleum', 'repair_kits',
--                                   'item_qty:22500' (item quantity across bags + retainers)
--   'fc'        + free company id   e.g. metric 'fc_points', 'item_qty:22500' (FC chest)
-- label is optional context stored with a value. For 'fc_points' it is the FC leader's name
-- (on the FC's own rows) or the FC's name (on the leader character's row), so same-named
-- FCs can be told apart. A changed label is stored as a new row even if the value is unchanged.
-- New charts only need the plugin to start writing a new metric name; no schema change.

CREATE TABLE IF NOT EXISTS companion_metric_history (
    recorded_at  timestamptz NOT NULL DEFAULT now(),
    subject_type text        NOT NULL,
    subject_id   numeric     NOT NULL,
    metric       text        NOT NULL,
    value        numeric     NOT NULL,
    label        text
);

-- One subject's metric over time (a single character's gil).
CREATE INDEX IF NOT EXISTS companion_metric_history_subject_idx
    ON companion_metric_history (subject_type, subject_id, metric, recorded_at DESC);

-- One metric across everyone over time (fleet-wide gil).
CREATE INDEX IF NOT EXISTS companion_metric_history_metric_idx
    ON companion_metric_history (metric, recorded_at DESC);

CREATE TABLE IF NOT EXISTS companion_metric_latest (
    subject_type text        NOT NULL,
    subject_id   numeric     NOT NULL,
    metric       text        NOT NULL,
    value        numeric     NOT NULL,
    recorded_at  timestamptz NOT NULL,
    label        text,
    PRIMARY KEY (subject_type, subject_id, metric)
);

-- If an earlier version of this script created the tables without label, add it.
ALTER TABLE companion_metric_history ADD COLUMN IF NOT EXISTS label text;
ALTER TABLE companion_metric_latest  ADD COLUMN IF NOT EXISTS label text;
