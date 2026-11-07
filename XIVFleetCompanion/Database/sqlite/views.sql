-- The fleet views (db/views.sql) in SQLite's words, for the plugin-only one-file version planned near the end of 2.x.
-- Each view has the same name and columns and must give the same answers as its PostgreSQL twin:
-- tests/db/verify-sqlite-views.js fills both with the same data and compares every row. Change both files together.
--
-- What differs from PostgreSQL, and how:
--   times are Unix seconds (db/sqlite/tables.sql), so "now" is NOW_SECONDS below and spans are seconds
--     ((julianday('now') - 2440587.5) * 86400, the same within one statement); fleet_not_synced_view's "behind" is
--     seconds where PostgreSQL gives an interval
--   no functions: the part letters are a list inside fleet_subs_view instead of fleet_part_letter()
--   no LATERAL or DISTINCT ON: window functions and correlated subqueries instead
--   no greatest/least, which skip NULLs: CASE, or max()/min() where nothing can be NULL
--   no ceil/floor (not always built in): CAST to an integer, which cuts towards zero; PostgreSQL's ::int rounds, so
--     round() first where it does
--   companion_metric_history.subject_id has no type (a character's id is a whole number, a Free Company's text), so
--     what it's matched with is written +x: that keeps SQLite from converting either side, which would stop it using
--     the history's index (a check of the supplies took seconds instead of milliseconds)
--   booleans are 0 or 1
--   text sorts letter by letter here, but by the database's language setting in PostgreSQL: any order that reaches the
--     game (fleet_alerts_view's, and the plugin's own ORDER BY when it reads) sorts text with COLLATE "C" there

DROP VIEW IF EXISTS fleet_settings_view;
DROP VIEW IF EXISTS fleet_shopping_held_view;
DROP VIEW IF EXISTS fleet_shopping_progress_view;
DROP VIEW IF EXISTS fleet_voyage_returns_view;
DROP VIEW IF EXISTS fleet_rankup_history_view;
DROP VIEW IF EXISTS fleet_rankup_forecast_view;
DROP VIEW IF EXISTS fleet_voyage_loot_view;
DROP VIEW IF EXISTS fleet_voyage_sectors_view;
DROP VIEW IF EXISTS fleet_sub_history_view;
DROP VIEW IF EXISTS fleet_route_fight_club_view;
DROP VIEW IF EXISTS fleet_route_subs_view;
DROP VIEW IF EXISTS fleet_market_listings_view;
DROP VIEW IF EXISTS fleet_market_accounts_view;
DROP VIEW IF EXISTS fleet_market_settings_view;
DROP VIEW IF EXISTS fleet_parts_spares_view;
DROP VIEW IF EXISTS fleet_parts_fight_club_view;
DROP VIEW IF EXISTS fleet_parts_rules_view;
DROP VIEW IF EXISTS fleet_parts_subs_view;
DROP VIEW IF EXISTS fleet_parts_characters_view;
DROP VIEW IF EXISTS fleet_venture_income_view;
DROP VIEW IF EXISTS fleet_income_view;
DROP VIEW IF EXISTS fleet_salvage_view;
DROP VIEW IF EXISTS fleet_item_totals_view;
DROP VIEW IF EXISTS fleet_holdings_view;
DROP VIEW IF EXISTS fleet_fcs_view;
DROP VIEW IF EXISTS fleet_pcs_view;
DROP VIEW IF EXISTS fleet_retainer_gear_view;
DROP VIEW IF EXISTS fleet_retainers_view;
DROP VIEW IF EXISTS fleet_roster_view;
DROP VIEW IF EXISTS fleet_alerts_view;
DROP VIEW IF EXISTS fleet_summary_view;
DROP VIEW IF EXISTS fleet_retainer_counts_view;
DROP VIEW IF EXISTS fleet_not_synced_view;
DROP VIEW IF EXISTS fleet_subs_view;
DROP VIEW IF EXISTS fleet_supplies_view;

-- NOW_SECONDS: ((julianday('now') - 2440587.5) * 86400.0)

CREATE VIEW fleet_supplies_view AS
WITH settings AS (
  SELECT window_days, target_days, ((julianday('now') - 2440587.5) * 86400.0) - window_days * 86400.0 AS window_start
  FROM (SELECT
    coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
              WHERE setting_key = 'supply_window_days' AND setting_value IN (3, 7, 14, 30)), 14) AS window_days,
    coalesce((SELECT CAST(round(setting_value) AS INTEGER) FROM fleet_economics_settings
              WHERE setting_key = 'restock_target_days' AND setting_value BETWEEN 1 AND 90), 14) AS target_days)
),
characters AS (
  SELECT c.cid, c.name, c.world, c.account_label, c.fc_id, so.sort_index, c.last_synced_at AS as_of,
         coalesce(c.ceruleum, 0) AS ceruleum, coalesce(c.repair_kits, 0) AS repair_kits
  FROM companion_character_current c
  LEFT JOIN character_sort_order so ON so.cid = c.cid
  WHERE EXISTS (SELECT 1 FROM companion_submarine_snapshot s WHERE s.cid = c.cid AND s.level > 0)
),
series AS (
  SELECT row_number() OVER (ORDER BY ch.cid, m.metric) AS id, ch.cid, ch.as_of, m.supply, m.metric, m.held,
         CASE WHEN NOT m.held THEN CASE m.supply WHEN 'ceruleum' THEN ch.ceruleum ELSE ch.repair_kits END END AS now_value
  FROM characters ch
  CROSS JOIN (SELECT 'ceruleum' AS supply, 'item_qty:10155' AS metric, 1 AS held
              UNION ALL SELECT 'ceruleum', 'ceruleum', 0
              UNION ALL SELECT 'repair_kits', 'item_qty:10373', 1
              UNION ALL SELECT 'repair_kits', 'repair_kits', 0) m
),
-- Everything recorded in the window, up to the last sync, newest first per series.
inside AS (
  SELECT se.id, h.recorded_at AS at, h.value,
         lead(h.value) OVER (PARTITION BY se.id ORDER BY h.recorded_at DESC) AS older,
         row_number() OVER (PARTITION BY se.id ORDER BY h.recorded_at DESC) AS rn
  FROM series se CROSS JOIN settings st
  JOIN companion_metric_history h ON h.subject_type = 'character' AND h.subject_id = +se.cid AND h.metric = se.metric
   AND h.recorded_at > st.window_start AND h.recorded_at <= se.as_of
),
w AS (
  SELECT id, sum(CASE WHEN older > value THEN older - value ELSE 0 END) AS drops, min(at) AS oldest_at,
         max(CASE WHEN older IS NULL THEN value END) AS oldest_value,  -- the last one read has nothing older
         max(CASE WHEN rn = 1 THEN value END) AS newest_value          -- the first one read
  FROM inside GROUP BY id
),
used AS (
  SELECT se.id, se.now_value, se.as_of, st.window_start,
         -- the starting point: the last value at or before the window start (and the last sync)
         (SELECT h.recorded_at FROM companion_metric_history h
          WHERE h.subject_type = 'character' AND h.subject_id = +se.cid AND h.metric = se.metric
            AND h.recorded_at <= min(st.window_start, se.as_of) ORDER BY h.recorded_at DESC LIMIT 1) AS base_at,
         (SELECT h.value FROM companion_metric_history h
          WHERE h.subject_type = 'character' AND h.subject_id = +se.cid AND h.metric = se.metric
            AND h.recorded_at <= min(st.window_start, se.as_of) ORDER BY h.recorded_at DESC LIMIT 1) AS base_value,
         coalesce(w.drops, 0) AS drops, w.oldest_at, w.oldest_value, w.newest_value
  FROM series se CROSS JOIN settings st LEFT JOIN w ON w.id = se.id
),
totals AS (
  SELECT id, as_of, window_start,
         coalesce(base_at, oldest_at, CASE WHEN now_value IS NOT NULL THEN as_of END) AS first_at,
         drops
         + CASE WHEN base_value IS NOT NULL AND oldest_value IS NOT NULL AND base_value > oldest_value THEN base_value - oldest_value ELSE 0 END
         + CASE WHEN now_value IS NOT NULL AND as_of > window_start AND coalesce(newest_value, base_value) > now_value
                THEN coalesce(newest_value, base_value) - now_value ELSE 0 END AS used
  FROM used
),
rates AS (
  SELECT se.cid, se.supply, se.held,
         CASE WHEN t.first_at IS NOT NULL  -- no values at all: not enough history
                   AND (t.as_of - max(t.window_start, t.first_at)) / 86400.0 >= 1
              THEN t.used * 1.0 / ((t.as_of - max(t.window_start, t.first_at)) / 86400.0) END AS per_day
  FROM series se JOIN totals t ON t.id = se.id
),
per_day AS (
  SELECT ch.*,
         coalesce((SELECT per_day FROM rates r WHERE r.cid = ch.cid AND r.supply = 'ceruleum' AND r.held),
                  (SELECT per_day FROM rates r WHERE r.cid = ch.cid AND r.supply = 'ceruleum' AND NOT r.held)) AS ceruleum_per_day,
         coalesce((SELECT per_day FROM rates r WHERE r.cid = ch.cid AND r.supply = 'repair_kits' AND r.held),
                  (SELECT per_day FROM rates r WHERE r.cid = ch.cid AND r.supply = 'repair_kits' AND NOT r.held)) AS repair_kits_per_day
  FROM characters ch
),
-- What to buy for the restock days, before rounding up.
short AS (
  SELECT p.*, st.window_days, st.target_days,
         p.ceruleum_per_day * st.target_days - p.ceruleum AS ceruleum_short,
         p.repair_kits_per_day * st.target_days - p.repair_kits AS repair_kits_short
  FROM per_day p CROSS JOIN settings st
)
SELECT cid, name, world, account_label, fc_id, sort_index, as_of AS last_synced_at,
       ceruleum, ceruleum_per_day,
       CASE WHEN ceruleum_per_day > 0 THEN max(ceruleum, 0) / ceruleum_per_day END AS ceruleum_days_left,
       CASE WHEN ceruleum_per_day > 0 THEN max(0, CAST(ceruleum_short AS INTEGER) + (ceruleum_short > CAST(ceruleum_short AS INTEGER))) ELSE 0 END AS ceruleum_restock,
       repair_kits, repair_kits_per_day,
       CASE WHEN repair_kits_per_day > 0 THEN max(repair_kits, 0) / repair_kits_per_day END AS repair_kits_days_left,
       CASE WHEN repair_kits_per_day > 0 THEN max(0, CAST(repair_kits_short AS INTEGER) + (repair_kits_short > CAST(repair_kits_short AS INTEGER))) ELSE 0 END AS repair_kits_restock,
       window_days, target_days
FROM short;

CREATE VIEW fleet_subs_view AS
WITH letters (lo, hi, letter) AS (
  VALUES (21792, 21795, 'S'), (21796, 21799, 'U'), (22526, 22529, 'W'), (23903, 23906, 'C'), (24344, 24347, 'Y'),
         (24348, 24351, 'S+'), (24352, 24355, 'U+'), (24356, 24359, 'W+'), (24360, 24363, 'C+'), (24364, 24367, 'Y+')
)
SELECT s.cid, c.name AS character_name, c.world, c.account_label, c.fc_id, so.sort_index,
       s.sub_name,
       coalesce(s.slot, CASE WHEN s.sub_name GLOB 'Submersible-[1-4]' THEN CAST(substr(s.sub_name, 13, 1) AS INTEGER) END) AS slot,
       s.level AS rank,
       coalesce((SELECT letter FROM letters WHERE s.part1 BETWEEN lo AND hi), CAST(s.part1 AS TEXT), 'null') || '-' ||
       coalesce((SELECT letter FROM letters WHERE s.part2 BETWEEN lo AND hi), CAST(s.part2 AS TEXT), 'null') || '-' ||
       coalesce((SELECT letter FROM letters WHERE s.part3 BETWEEN lo AND hi), CAST(s.part3 AS TEXT), 'null') || '-' ||
       coalesce((SELECT letter FROM letters WHERE s.part4 BETWEEN lo AND hi), CAST(s.part4 AS TEXT), 'null') AS build,
       s.return_time,
       CASE WHEN coalesce(s.return_time, 0) <= 0 THEN 'in_port'
            WHEN s.return_time <= ((julianday('now') - 2440587.5) * 86400.0) THEN 'returned'
            ELSE 'out' END AS status,
       s.current_exp, s.next_level_exp, s.updated_at
FROM companion_submarine_snapshot s
LEFT JOIN companion_character_current c ON c.cid = s.cid
LEFT JOIN character_sort_order so ON so.cid = s.cid
WHERE s.level > 0;

CREATE VIEW fleet_not_synced_view AS
WITH setting AS (
  SELECT coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
                   WHERE setting_key = 'not_synced_after_minutes' AND setting_value = round(setting_value)
                     AND setting_value BETWEEN 10 AND 1440), 30) AS minutes
),
accounts AS (
  SELECT coalesce(nullif(account_label, ''), 'Ungrouped') AS account, max(last_synced_at) AS newest
  FROM companion_character_current GROUP BY 1
)
SELECT c.cid, c.name, c.world, c.account_label, a.newest - c.last_synced_at AS behind
FROM companion_character_current c
JOIN accounts a ON a.account = coalesce(nullif(c.account_label, ''), 'Ungrouped')
CROSS JOIN setting st
WHERE a.newest - c.last_synced_at >= st.minutes * 60;

-- The retainers with a class right now, as the app's Summary (characters/retainer-summary.js): how many there are,
-- ventures done and waiting to be collected, away on one, and without one. Only retainers of characters in the Roster
-- that the plugin still syncs. A venture with no end time counts as away, as there. Small and quick, so the plugin
-- reads it with the subs (every 30 seconds): a retainer caught between two ventures while AutoRetainer collects and
-- sends it again clears at the next read after the next sync.
CREATE VIEW fleet_retainer_counts_view AS
SELECT count(*) AS retainers,
       count(*) FILTER (WHERE rl.has_venture AND coalesce(rl.venture_ends_at, 0) <> 0 AND rl.venture_ends_at <= ((julianday('now') - 2440587.5) * 86400.0)) AS ventures_done,
       count(*) FILTER (WHERE rl.has_venture AND NOT (coalesce(rl.venture_ends_at, 0) <> 0 AND rl.venture_ends_at <= ((julianday('now') - 2440587.5) * 86400.0))) AS retainers_away,
       count(*) FILTER (WHERE NOT coalesce(rl.has_venture, 0)) AS retainers_idle
FROM companion_retainer_lookup rl
JOIN companion_character_current c ON c.cid = rl.owner_cid
WHERE rl.job > 0
  AND rl.owner_cid NOT IN (SELECT cid FROM fleet_not_synced_view);

CREATE VIEW fleet_summary_view AS
WITH thresholds AS (
  SELECT
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_ceruleum_threshold'), 0) AS ceruleum,
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_repair_kit_threshold'), 0) AS repair_kits
),
subs AS (
  SELECT count(*) AS subs,
         count(*) FILTER (WHERE status = 'returned') AS subs_returned,
         count(*) FILTER (WHERE status = 'in_port') AS subs_in_port,
         count(*) FILTER (WHERE status = 'out') AS subs_out,
         min(return_time) FILTER (WHERE status = 'out') AS next_return_time
  FROM fleet_subs_view
),
retainers AS (
  SELECT retainers, ventures_done, retainers_idle FROM fleet_retainer_counts_view
),
supplies AS (
  SELECT count(*) FILTER (WHERE t.ceruleum > 0 AND f.ceruleum < t.ceruleum) AS low_ceruleum,
         count(*) FILTER (WHERE t.repair_kits > 0 AND f.repair_kits < t.repair_kits) AS low_repair_kits,
         min(CASE WHEN f.ceruleum_days_left IS NULL THEN f.repair_kits_days_left
                  WHEN f.repair_kits_days_left IS NULL THEN f.ceruleum_days_left
                  ELSE min(f.ceruleum_days_left, f.repair_kits_days_left) END) AS soonest_runs_out_days
  FROM fleet_supplies_view f CROSS JOIN thresholds t
  WHERE f.cid NOT IN (SELECT cid FROM fleet_not_synced_view)
)
SELECT subs.*, retainers.*, supplies.* FROM subs, retainers, supplies;

CREATE VIEW fleet_alerts_view AS
WITH settings AS (
  SELECT
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_ceruleum_threshold'), 0) AS ceruleum,
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_repair_kit_threshold'), 0) AS repair_kits,
    coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
              WHERE setting_key = 'house_warn_days' AND setting_value = round(setting_value) AND setting_value BETWEEN 1 AND 44), 7) AS house_days
),
houses AS (
  SELECT fc_id, fc_name, has_house FROM (
    SELECT fc_id, fc_name, has_house, row_number() OVER (PARTITION BY fc_id ORDER BY updated_at DESC) AS rn
    FROM companion_character_housing WHERE fc_id IS NOT NULL)
  WHERE rn = 1
),
visited AS (
  SELECT fc_id, max(house_last_visited) AS last_visited
  FROM companion_character_housing WHERE fc_id IS NOT NULL GROUP BY fc_id
),
house_ages AS (
  SELECT h.fc_id, coalesce(h.fc_name, 'FC ' || h.fc_id) AS fc_name, v.last_visited,
         CASE WHEN v.last_visited IS NOT NULL
              THEN max(0, CAST((((julianday('now') - 2440587.5) * 86400.0) - v.last_visited) / 86400 AS INTEGER)) END AS days
  FROM houses h LEFT JOIN visited v ON v.fc_id = h.fc_id
  WHERE h.has_house
),
supplies AS (
  SELECT c.cid, c.name || ' (' || c.world || ')' AS who, coalesce(c.ceruleum, 0) AS ceruleum, coalesce(c.repair_kits, 0) AS repair_kits
  FROM companion_character_current c
  WHERE EXISTS (SELECT 1 FROM companion_submarine_snapshot s WHERE s.cid = c.cid AND s.level > 0)
    AND c.cid NOT IN (SELECT cid FROM fleet_not_synced_view)
)
SELECT CASE WHEN days IS NULL OR days < 30 THEN 'warn' ELSE 'danger' END AS level, 'house' AS kind, fc_name AS subject,
       CASE WHEN days IS NULL THEN 'house: no date yet for when it was last entered'
            WHEN days >= 45 THEN 'house not entered for ' || days || ' days: past 45, the game can demolish it any time'
            ELSE 'house not entered for ' || days || ' day' || CASE WHEN days = 1 THEN '' ELSE 's' END
                 || '; demolished after ' || (45 - days) || ' more' END AS detail,
       coalesce(days, 0) AS weight
FROM house_ages, settings st WHERE days IS NULL OR days >= st.house_days
UNION ALL
SELECT 'warn', 'low_ceruleum', s.who, s.ceruleum || ' ceruleum (warning below ' || CAST(round(st.ceruleum) AS INTEGER) || ')', 0
FROM supplies s, settings st WHERE st.ceruleum > 0 AND s.ceruleum < st.ceruleum
UNION ALL
SELECT 'warn', 'low_repair_kits', s.who, s.repair_kits || ' repair kits (warning below ' || CAST(round(st.repair_kits) AS INTEGER) || ')', 0
FROM supplies s, settings st WHERE st.repair_kits > 0 AND s.repair_kits < st.repair_kits
UNION ALL
SELECT 'warn', 'not_synced', name || ' (' || world || ')',
       'no longer synced (' || CAST(behind / 3600 AS INTEGER) || ' h behind its account): unticked in the plugin, or its AutoRetainer no longer has it', 0
FROM fleet_not_synced_view
UNION ALL
SELECT 'warn', 'autoretainer', pc_name || CASE WHEN account_label <> '' THEN ' (' || account_label || ')' ELSE '' END,
       'AutoRetainer was not running at its last sync, so nothing syncs from there', 0
FROM companion_pc_status WHERE autoretainer_ready = 0 AND last_sync_at > ((julianday('now') - 2440587.5) * 86400.0) - 86400
ORDER BY 1, 2, 5 DESC, 3;

CREATE VIEW fleet_roster_view AS
WITH thresholds AS (
  SELECT
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_ceruleum_threshold'), 0) AS ceruleum,
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_repair_kit_threshold'), 0) AS repair_kits
),
built AS (
  SELECT cid, count(*) AS subs_built FROM companion_submarine_snapshot WHERE level > 0 GROUP BY cid
)
SELECT c.cid, c.name, c.world, c.account_label, so.sort_index,
       c.gil, c.ceruleum, c.repair_kits, c.retainer_count, c.submarine_count, c.last_synced_at,
       h.fc_name, h.fc_points,
       CASE WHEN h.has_house THEN coalesce(k.city_name, 'City ' || h.house_city) || ' W' || h.house_ward || ' P' || h.house_plot END AS house,
       l.pc_name AS login_pc, l.account_label AS login_label, l.logged_in_at,
       CAST(coalesce(b.subs_built, 0) AS INTEGER) AS subs_built,
       CAST(ns.behind / 60 AS INTEGER) AS behind_minutes,
       (coalesce(b.subs_built, 0) > 0 AND ns.cid IS NULL AND coalesce(c.ceruleum, 0) < t.ceruleum) AS low_ceruleum,
       (coalesce(b.subs_built, 0) > 0 AND ns.cid IS NULL AND coalesce(c.repair_kits, 0) < t.repair_kits) AS low_repair_kits,
       c.num_sub_slots, t.ceruleum AS ceruleum_warning, t.repair_kits AS repair_kit_warning
FROM companion_character_current c
CROSS JOIN thresholds t
LEFT JOIN character_sort_order so ON so.cid = c.cid
LEFT JOIN companion_character_housing h ON h.cid = c.cid
LEFT JOIN housing_key_city_lookup k ON k.city_id = h.house_city
LEFT JOIN companion_character_login l ON l.cid = c.cid
LEFT JOIN built b ON b.cid = c.cid
LEFT JOIN fleet_not_synced_view ns ON ns.cid = c.cid;

CREATE VIEW fleet_retainers_view AS
SELECT rl.retainer_id, rl.owner_cid, c.name AS owner_name, c.world, c.account_label, so.sort_index,
       rl.name AS retainer_name, rl.job, rl.level, rl.gil, rl.has_venture, rl.venture_id, rl.venture_ends_at, rl.hire_order_index,
       t.target_mode, t.target_value, coalesce(t.only_crafted, 0) AS only_crafted,
       (rl.items_seen_at IS NOT NULL OR EXISTS (SELECT 1 FROM companion_inventory_snapshot inv
                                                WHERE inv.owner_cid = rl.owner_cid AND inv.retainer_id = rl.retainer_id)) AS items_seen
FROM companion_retainer_lookup rl
LEFT JOIN companion_character_current c ON c.cid = rl.owner_cid
LEFT JOIN retainer_gear_targets t ON t.retainer_id = rl.retainer_id
LEFT JOIN character_sort_order so ON so.cid = rl.owner_cid;

CREATE VIEW fleet_retainer_gear_view AS
SELECT retainer_id, item_id FROM companion_inventory_snapshot WHERE sorted_container = 11000 AND item_id IS NOT NULL;

CREATE VIEW fleet_pcs_view AS
SELECT pc_name, account_label, plugin_version, last_sync_at, autoretainer_ready, allagantools_ready, fctracker_found,
       submarinetracker_found, venture_stats_found, characters_synced, characters_left_out, characters_elsewhere,
       problems, check_text, check_to_fix, check_at, last_read_at
FROM companion_pc_status;

-- Every Free Company, as the Free Companies tab (characters/fc-directory-rows.js): its newest details (from whichever
-- member's row was updated last), leader, points, rank, members, house, when a member last entered it and how that
-- compares with the house warning (house_level: 'ok', 'warn', 'danger' from 30 days, 'unknown' with no date; NULL
-- without a house), and, without a house, when it can bid (eligibility: 'Eligible', 'Eligible in N days' or 'Rank too
-- low (N/6)', 30 days after founding or FC Tracker's override date, at rank 6). home_world_id is a World number the
-- plugin names. account_label: the first of its members' account labels; sort_index: its first member in the Roster.
CREATE VIEW fleet_fcs_view AS
WITH settings AS (
  SELECT coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
                   WHERE setting_key = 'house_warn_days' AND setting_value = round(setting_value) AND setting_value BETWEEN 1 AND 44), 7) AS house_days
),
newest AS (
  SELECT * FROM (
    SELECT h.*, row_number() OVER (PARTITION BY fc_id ORDER BY updated_at DESC) AS rn
    FROM companion_character_housing h WHERE fc_id IS NOT NULL) ranked
  WHERE rn = 1
),
visited AS (
  SELECT fc_id, max(house_last_visited) AS house_last_visited
  FROM companion_character_housing WHERE fc_id IS NOT NULL GROUP BY fc_id
),
members AS (
  SELECT c.fc_id, min(c.account_label) AS account_label, min(so.sort_index) AS sort_index
  FROM companion_character_current c LEFT JOIN character_sort_order so ON so.cid = c.cid
  WHERE c.fc_id IS NOT NULL GROUP BY c.fc_id
),
fcs AS (
  SELECT n.fc_id, n.fc_name, n.fc_master, n.fc_points, n.fc_rank, n.total_members, n.has_house,
         n.house_city, n.house_ward, n.house_plot, k.city_name AS district_name, n.fc_home_world_id AS home_world_id,
         n.fc_founding_date, n.fc_eligibility_override, v.house_last_visited, m.account_label, m.sort_index,
         coalesce(n.fc_eligibility_override, n.fc_founding_date) AS reference,
         CASE WHEN ltrim(n.fc_rank, ' ' || char(9) || char(10) || char(13)) GLOB '[0-9]*'
              THEN CAST(ltrim(n.fc_rank, ' ' || char(9) || char(10) || char(13)) AS INTEGER) END AS rank_number,
         CASE WHEN n.has_house AND v.house_last_visited IS NOT NULL
              THEN max(0, CAST((((julianday('now') - 2440587.5) * 86400.0) - v.house_last_visited) / 86400 AS INTEGER)) END AS house_days
  FROM newest n
  LEFT JOIN visited v ON v.fc_id = n.fc_id
  LEFT JOIN housing_key_city_lookup k ON k.city_id = n.house_city
  LEFT JOIN members m ON m.fc_id = n.fc_id
),
ages AS (
  SELECT f.*, (((julianday('now') - 2440587.5) * 86400.0) - f.reference) / 86400 AS days_since_founding FROM fcs f
)
SELECT a.fc_id, a.fc_name, a.fc_master, a.fc_points, a.fc_rank, a.total_members, a.has_house,
       CASE WHEN a.has_house THEN coalesce(a.district_name, 'City ' || a.house_city) || ' W' || a.house_ward || ' P' || a.house_plot END AS house,
       a.home_world_id, a.house_last_visited, a.house_days,
       CASE WHEN NOT a.has_house OR a.has_house IS NULL THEN NULL
            WHEN a.house_days IS NULL THEN 'unknown'
            WHEN a.house_days >= 30 THEN 'danger'
            WHEN a.house_days >= st.house_days THEN 'warn'
            ELSE 'ok' END AS house_level,
       CASE WHEN a.has_house AND a.house_days IS NOT NULL THEN max(0, 45 - a.house_days) END AS demolition_in_days,
       CASE WHEN NOT coalesce(a.has_house, 0) AND a.reference IS NOT NULL THEN a.reference + 30 * 86400 END AS eligible_at,
       CASE WHEN coalesce(a.has_house, 0) OR a.reference IS NULL THEN NULL
            WHEN a.rank_number >= 6 AND a.days_since_founding >= 30 THEN 'Eligible'
            WHEN a.days_since_founding < 30 THEN 'Eligible in ' || (CAST(30 - a.days_since_founding AS INTEGER) + (30 - a.days_since_founding > CAST(30 - a.days_since_founding AS INTEGER))) || ' day'
                 || CASE WHEN CAST(30 - a.days_since_founding AS INTEGER) + (30 - a.days_since_founding > CAST(30 - a.days_since_founding AS INTEGER)) = 1 THEN '' ELSE 's' END
            ELSE 'Rank too low (' || coalesce(CAST(a.rank_number AS TEXT), '?') || '/6)' END AS eligibility,
       a.account_label, a.sort_index
FROM ages a CROSS JOIN settings st;

-- Every stack of every item the fleet holds, one row each, for the in-game Inventory (characters, Free Companies, the
-- whole fleet, and Where is it?). kind: 'character' (its own bags and so on), 'retainer' or 'fc' (a Free Company
-- chest). item_id is the plain item, with hq for a high-quality one (the game stores those 1,000,000 higher).
-- place says where in plain words, as Where is it? (characters/item-locations.js placeLabel); container_name is the
-- app's own name for the container. money: a container that holds gil, not items. For a Free Company chest, owner_cid,
-- owner_name, account_label and sort_index are those of the member the chest is counted on (the leader when tracked,
-- else the first in the Roster: characters/fc-chest.js chestHolders), so it sorts with that character.
CREATE VIEW fleet_holdings_view AS
WITH fc_names AS (
  SELECT fc_id, fc_name FROM (
    SELECT fc_id, fc_name, row_number() OVER (PARTITION BY fc_id ORDER BY updated_at DESC) AS rn
    FROM companion_character_housing WHERE fc_id IS NOT NULL) ranked
  WHERE rn = 1
),
holders AS (
  SELECT fc_id, cid FROM (
    SELECT c.fc_id, c.cid,
           row_number() OVER (PARTITION BY c.fc_id
             ORDER BY CASE WHEN h.fc_master IS NOT NULL AND c.name IS NOT NULL AND lower(c.name) = lower(h.fc_master) THEN 0 ELSE 1 END,
                      so.sort_index NULLS LAST, c.name, CAST(c.cid AS TEXT)) AS rn
    FROM companion_character_current c
    LEFT JOIN character_sort_order so ON so.cid = c.cid
    LEFT JOIN companion_character_housing h ON h.cid = c.cid AND h.fc_id = c.fc_id
    WHERE c.fc_id IS NOT NULL AND c.fc_id <> 0) ranked
  WHERE rn = 1
),
stacks AS (
  SELECT CASE WHEN inv.retainer_id = inv.owner_cid THEN 'character' ELSE 'retainer' END AS kind,
         inv.owner_cid, inv.retainer_id, NULL AS fc_id, inv.sorted_container AS container, inv.item_id, inv.quantity,
         rl.name AS retainer_name, rl.hire_order_index, til.display_name AS container_name,
         coalesce(c.name, 'Character ' || inv.owner_cid) AS who, c.world, c.name AS owner_name, c.account_label, so.sort_index
  FROM companion_inventory_snapshot inv
  LEFT JOIN companion_character_current c ON c.cid = inv.owner_cid
  LEFT JOIN character_sort_order so ON so.cid = inv.owner_cid
  LEFT JOIN companion_retainer_lookup rl ON rl.retainer_id = inv.retainer_id
  LEFT JOIN companion_inventory_type_lookup til ON til.sorted_container = inv.sorted_container
  WHERE inv.quantity > 0
  UNION ALL
  SELECT 'fc', ho.cid, NULL, inv.fc_id, inv.sorted_container, inv.item_id, inv.quantity,
         NULL, NULL, til.display_name,
         coalesce(fn.fc_name, 'Free Company ' || inv.fc_id), NULL, c.name, c.account_label, so.sort_index
  FROM companion_fc_inventory_snapshot inv
  LEFT JOIN fc_names fn ON fn.fc_id = inv.fc_id
  LEFT JOIN holders ho ON ho.fc_id = inv.fc_id
  LEFT JOIN companion_character_current c ON c.cid = ho.cid
  LEFT JOIN character_sort_order so ON so.cid = ho.cid
  LEFT JOIN companion_inventory_type_lookup til ON til.sorted_container = inv.sorted_container
  WHERE inv.quantity > 0
)
SELECT kind, owner_cid, retainer_id, fc_id, container,
       CASE WHEN item_id >= 1000000 AND item_id < 2000000 THEN item_id - 1000000 ELSE item_id END AS item_id,
       (item_id >= 1000000 AND item_id < 2000000) AS hq,
       quantity, who, world, owner_name, account_label, sort_index, retainer_name, hire_order_index, container_name,
       container IN (2000, 2502, 12000, 22000) AS money,
       (kind <> 'fc' AND (container BETWEEN 1000 AND 1001 OR container = 11000)) AS equipped,
       CASE kind
         WHEN 'fc' THEN
           CASE WHEN container BETWEEN 20000 AND 20010 THEN 'FC chest tab ' || (container - 20000 + 1)
                WHEN container = 22001 THEN 'FC crystals'
                ELSE coalesce(nullif(container_name, ''), 'FC container ' || container) END
         WHEN 'retainer' THEN
           'Retainer ' || coalesce(nullif(retainer_name, ''), 'unknown') ||
           CASE WHEN container BETWEEN 10000 AND 10006 THEN ''
                WHEN container = 11000 THEN ' (equipped)'
                WHEN container = 12001 THEN ' (crystals)'
                WHEN container = 12002 THEN ' (for sale)'
                ELSE ' (' || coalesce(nullif(container_name, ''), CAST(container AS TEXT)) || ')' END
         ELSE
           CASE WHEN container BETWEEN 0 AND 3 THEN 'Bags'
                WHEN container BETWEEN 1000 AND 1001 THEN 'Equipped'
                WHEN container = 2001 THEN 'Crystals'
                WHEN container = 2003 THEN 'Mail'
                WHEN container = 2004 THEN 'Key items'
                WHEN container = 2500 THEN 'Armoire'
                WHEN container = 2501 THEN 'Glamour dresser'
                WHEN container BETWEEN 3200 AND 3500 THEN 'Armoury chest'
                WHEN container BETWEEN 4000 AND 4001 THEN 'Saddlebag'
                WHEN container BETWEEN 4100 AND 4101 THEN 'Premium saddlebag'
                WHEN container >= 25000 AND container < 28000 THEN 'House: ' || coalesce(nullif(container_name, ''), CAST(container AS TEXT))
                ELSE coalesce(nullif(container_name, ''), 'Container ' || container) END
       END AS place
FROM stacks;

CREATE VIEW fleet_item_totals_view AS
SELECT item_id, hq, sum(quantity) AS total, sum(CASE WHEN equipped THEN quantity ELSE 0 END) AS equipped
FROM fleet_holdings_view
WHERE NOT (kind <> 'fc' AND container = 2000)
GROUP BY item_id, hq;

CREATE VIEW fleet_salvage_view AS
WITH prices (item_id, gil) AS (
  VALUES (22500, 8000), (22501, 9000), (22502, 10000), (22503, 13000),
         (22504, 27000), (22505, 28500), (22506, 30000), (22507, 34500)
),
held AS (
  SELECT h.owner_cid AS cid, h.item_id, sum(h.quantity) AS quantity
  FROM fleet_holdings_view h
  WHERE h.item_id BETWEEN 22500 AND 22507 AND NOT h.hq
  GROUP BY h.owner_cid, h.item_id
)
SELECT c.cid, c.name, c.world, c.account_label, so.sort_index,
       sum(CASE WHEN h.item_id <= 22503 THEN h.quantity ELSE 0 END) AS standard,
       sum(CASE WHEN h.item_id > 22503 THEN h.quantity ELSE 0 END) AS extravagant,
       sum(h.quantity * p.gil) AS gil,
       sum(CASE WHEN h.item_id = 22500 THEN h.quantity ELSE 0 END) AS q22500,
       sum(CASE WHEN h.item_id = 22501 THEN h.quantity ELSE 0 END) AS q22501,
       sum(CASE WHEN h.item_id = 22502 THEN h.quantity ELSE 0 END) AS q22502,
       sum(CASE WHEN h.item_id = 22503 THEN h.quantity ELSE 0 END) AS q22503,
       sum(CASE WHEN h.item_id = 22504 THEN h.quantity ELSE 0 END) AS q22504,
       sum(CASE WHEN h.item_id = 22505 THEN h.quantity ELSE 0 END) AS q22505,
       sum(CASE WHEN h.item_id = 22506 THEN h.quantity ELSE 0 END) AS q22506,
       sum(CASE WHEN h.item_id = 22507 THEN h.quantity ELSE 0 END) AS q22507
FROM held h
JOIN prices p ON p.item_id = h.item_id
JOIN companion_character_current c ON c.cid = h.cid
LEFT JOIN character_sort_order so ON so.cid = c.cid
GROUP BY c.cid, c.name, c.world, c.account_label, so.sort_index;

CREATE VIEW fleet_income_view AS
WITH settings AS (
  SELECT window_days, kit_cost, ((julianday('now') - 2440587.5) * 86400.0) - window_days * 86400.0 AS window_start
  FROM (SELECT
    coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
              WHERE setting_key = 'income_window_days' AND setting_value IN (3, 7, 14, 30)), 7) AS window_days,
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'repair_kit_cost'), 0) AS kit_cost) chosen
),
prices (item_id, gil) AS (
  VALUES (22500, 8000), (22501, 9000), (22502, 10000), (22503, 13000),
         (22504, 27000), (22505, 28500), (22506, 30000), (22507, 34500)
),
characters AS (
  SELECT c.cid, c.name, c.world, c.account_label, c.fc_id, so.sort_index, c.last_synced_at,
         (SELECT count(*) FROM companion_submarine_snapshot s WHERE s.cid = c.cid AND s.level > 0) AS subs
  FROM companion_character_current c
  LEFT JOIN character_sort_order so ON so.cid = c.cid
  WHERE EXISTS (SELECT 1 FROM companion_submarine_snapshot s WHERE s.cid = c.cid AND s.level > 0)
),
metrics (metric) AS (
  VALUES ('item_qty:22500'), ('item_qty:22501'), ('item_qty:22502'), ('item_qty:22503'),
         ('item_qty:22504'), ('item_qty:22505'), ('item_qty:22506'), ('item_qty:22507'),
         ('repair_kits'), ('item_qty:10373'), ('inventory_sources')
),
subjects AS (
  SELECT 'character' AS subject_type, cid AS subject_id FROM characters
  UNION SELECT 'fc', fc_id FROM characters WHERE fc_id IS NOT NULL
),
-- The last value at or before the window start, and everything after it.
points AS (
  SELECT h.subject_type, h.subject_id, h.metric, h.recorded_at, h.value
  FROM subjects s CROSS JOIN settings st
  JOIN companion_metric_history h ON h.subject_type = s.subject_type AND h.subject_id = +s.subject_id
  WHERE h.recorded_at > st.window_start AND h.metric IN (SELECT metric FROM metrics)
  UNION ALL
  SELECT h.subject_type, h.subject_id, h.metric, h.recorded_at, h.value
  FROM (SELECT s.subject_type, s.subject_id, m.metric,
               (SELECT max(b.recorded_at) FROM companion_metric_history b
                WHERE b.subject_type = s.subject_type AND b.subject_id = +s.subject_id AND b.metric = m.metric
                  AND b.recorded_at <= st.window_start) AS at
        FROM subjects s CROSS JOIN metrics m CROSS JOIN settings st) base
  JOIN companion_metric_history h ON h.subject_type = base.subject_type AND h.subject_id = +base.subject_id
   AND h.metric = base.metric AND h.recorded_at = base.at
),
series AS (
  SELECT subject_type, subject_id, metric, recorded_at AS at, value,
         lag(value) OVER (PARTITION BY subject_type, subject_id, metric ORDER BY recorded_at) AS prev
  FROM points
),
reach AS (
  SELECT subject_id AS cid, max(at) AS newest_at,
         min(CASE WHEN metric LIKE 'item_qty:225%' THEN at END) AS salvage_first,
         min(CASE WHEN metric = 'item_qty:10373' THEN at END) AS kits_total_first
  FROM series WHERE subject_type = 'character'
  GROUP BY subject_id
),
ends AS (
  SELECT ch.cid, ch.fc_id, r.kits_total_first, r.salvage_first, st.window_start,
         coalesce(CASE WHEN ch.last_synced_at IS NULL THEN r.newest_at WHEN r.newest_at IS NULL THEN ch.last_synced_at
                       WHEN ch.last_synced_at > r.newest_at THEN ch.last_synced_at ELSE r.newest_at END,
                  ((julianday('now') - 2440587.5) * 86400.0)) AS end_at
  FROM characters ch CROSS JOIN settings st
  LEFT JOIN reach r ON r.cid = ch.cid
),
windows AS (
  SELECT cid, fc_id, kits_total_first, end_at, from_at, (end_at - from_at) / 86400.0 AS span_days
  FROM (SELECT e.*, CASE WHEN salvage_first IS NOT NULL THEN max(window_start, salvage_first) ELSE end_at END AS from_at FROM ends e)
),
new_sources AS (
  SELECT DISTINCT subject_id AS cid, at FROM series
  WHERE subject_type = 'character' AND metric = 'inventory_sources' AND value > prev
),
chest_drops AS (
  SELECT subject_id AS fc_id, metric, at, max(prev - value) AS fell FROM series
  WHERE subject_type = 'fc' AND prev > value
  GROUP BY subject_id, metric, at
),
rises AS (
  SELECT w.cid, CAST(substr(se.metric, 10) AS INTEGER) AS item_id, se.value - se.prev AS amount,
         ns.cid IS NOT NULL AS first_stored, coalesce(cd.fell, 0) AS chest_drop
  FROM windows w
  JOIN series se ON se.subject_type = 'character' AND se.subject_id = w.cid AND se.metric LIKE 'item_qty:225%'
  LEFT JOIN new_sources ns ON ns.cid = w.cid AND ns.at = se.at
  LEFT JOIN chest_drops cd ON cd.fc_id = w.fc_id AND cd.metric = se.metric AND cd.at = se.at
  WHERE se.at > w.from_at AND se.value > se.prev
),
salvage AS (
  SELECT r.cid,
         sum(CASE WHEN r.first_stored THEN 0 ELSE r.amount - min(r.amount, r.chest_drop) END) AS pieces,
         sum(CASE WHEN r.first_stored THEN 0 ELSE min(r.amount, r.chest_drop) END) AS from_chest,
         sum(CASE WHEN r.first_stored THEN r.amount ELSE 0 END) AS first_stored,
         sum(CASE WHEN r.first_stored THEN 0 ELSE (r.amount - min(r.amount, r.chest_drop)) * p.gil END) AS gil
  FROM rises r JOIN prices p ON p.item_id = r.item_id
  GROUP BY r.cid
),
kit_series AS (
  SELECT cid, from_at, CASE WHEN kits_total_first <= from_at THEN 'item_qty:10373' ELSE 'repair_kits' END AS metric
  FROM windows
),
kits AS (
  SELECT ks.cid, coalesce(sum(CASE WHEN se.at > ks.from_at AND se.prev > se.value THEN se.prev - se.value END), 0) AS used
  FROM kit_series ks
  LEFT JOIN series se ON se.subject_type = 'character' AND se.subject_id = ks.cid AND se.metric = ks.metric
  GROUP BY ks.cid
)
SELECT ch.cid, ch.name, ch.world, ch.account_label, ch.sort_index, ch.last_synced_at, ch.subs,
       w.span_days >= 1 AS enough, w.span_days,
       CASE WHEN w.span_days >= 1 THEN coalesce(sv.pieces, 0) END AS pieces,
       CASE WHEN w.span_days >= 1 THEN coalesce(sv.from_chest, 0) END AS from_chest,
       CASE WHEN w.span_days >= 1 THEN coalesce(sv.first_stored, 0) END AS first_stored,
       CASE WHEN w.span_days >= 1 THEN coalesce(sv.gil, 0) END AS salvage_gil,
       CASE WHEN w.span_days >= 1 THEN k.used END AS kits_used,
       CASE WHEN w.span_days >= 1 THEN k.used * st.kit_cost END AS kit_gil,
       CASE WHEN w.span_days >= 1 THEN coalesce(sv.gil, 0) - k.used * st.kit_cost END AS net,
       CASE WHEN w.span_days >= 1 THEN (coalesce(sv.gil, 0) - k.used * st.kit_cost) / w.span_days END AS per_day,
       CASE WHEN w.span_days >= 1 THEN coalesce(sv.gil, 0) / w.span_days END AS salvage_per_day,
       CASE WHEN w.span_days >= 1 THEN k.used * st.kit_cost / w.span_days END AS kit_per_day,
       st.window_days, st.kit_cost
FROM characters ch
JOIN windows w ON w.cid = ch.cid
JOIN kits k ON k.cid = ch.cid
LEFT JOIN salvage sv ON sv.cid = ch.cid
CROSS JOIN settings st;

CREATE VIEW fleet_venture_income_view AS
WITH settings AS (
  SELECT window_days, ((julianday('now') - 2440587.5) * 86400.0) - window_days * 86400.0 AS window_start
  FROM (SELECT coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
                         WHERE setting_key = 'income_window_days' AND setting_value IN (3, 7, 14, 30)), 7) AS window_days) chosen
),
rewards AS (
  SELECT v.* FROM companion_venture_result v CROSS JOIN settings st WHERE v.received_at > st.window_start
),
owners AS (
  SELECT owner_cid, count(*) AS rewards, count(DISTINCT retainer_name) AS retainers FROM rewards GROUP BY owner_cid
)
SELECT o.owner_cid, c.name, c.world, c.account_label, so.sort_index,
       i.item_id, i.quantity,
       CASE i.item_id WHEN 22500 THEN 8000 WHEN 22501 THEN 9000 WHEN 22502 THEN 10000 WHEN 22503 THEN 13000
                      WHEN 22504 THEN 27000 WHEN 22505 THEN 28500 WHEN 22506 THEN 30000 WHEN 22507 THEN 34500 END AS salvage_price,
       o.rewards, o.retainers, st.window_days, i.hq_quantity
FROM owners o
JOIN (SELECT owner_cid, item_id, sum(quantity) AS quantity, sum(CASE WHEN hq THEN quantity ELSE 0 END) AS hq_quantity FROM rewards GROUP BY owner_cid, item_id) i ON i.owner_cid = o.owner_cid
JOIN companion_character_current c ON c.cid = o.owner_cid
LEFT JOIN character_sort_order so ON so.cid = o.owner_cid
CROSS JOIN settings st;

-- ---- The Parts tab's inputs (db/views.sql has why: parts are worked out in the plugin) ----

CREATE VIEW fleet_parts_characters_view AS
SELECT c.cid, c.name, c.world, c.account_label, so.sort_index, c.fc_id,
       coalesce(c.num_sub_slots, 0) AS num_sub_slots, coalesce(h.has_house, 0) AS has_house
FROM companion_character_current c
LEFT JOIN character_sort_order so ON so.cid = c.cid
LEFT JOIN companion_character_housing h ON h.cid = c.cid;

CREATE VIEW fleet_parts_subs_view AS
SELECT s.cid, s.sub_name,
       coalesce(s.slot, CASE WHEN s.sub_name GLOB 'Submersible-[1-4]' THEN CAST(substr(s.sub_name, 13, 1) AS INTEGER) END) AS slot,
       s.level AS rank, s.part1, s.part2, s.part3, s.part4,
       coalesce(t.craft_enabled, 0) AS craft_enabled
FROM companion_submarine_snapshot s
LEFT JOIN sub_craft_toggle t ON t.cid = s.cid AND t.sub_name = s.sub_name
WHERE s.level > 0;

CREATE VIEW fleet_parts_rules_view AS
SELECT id, rank, cid_override, sub_filter, hull, stern, bow, bridge FROM sub_build_rules;

CREATE VIEW fleet_parts_fight_club_view AS
SELECT f.id, f.route, f.hours, f.repair_times, f.gil_sub_day, f.rec_build_notes,
       coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'repair_kit_cost'), 0) AS repair_kit_cost
FROM fight_club_routes f
WHERE f.rec_build_notes IS NOT NULL AND f.rec_build_notes <> '';

CREATE VIEW fleet_parts_spares_view AS
SELECT inv.owner_cid AS cid, CAST(NULL AS TEXT) AS fc_id, (inv.retainer_id <> inv.owner_cid) AS is_retainer, inv.retainer_id,
       rl.name AS retainer_name, inv.sorted_container, inv.sorted_slot_index, inv.item_id, inv.quantity
FROM companion_inventory_snapshot inv
LEFT JOIN companion_retainer_lookup rl ON rl.retainer_id = inv.retainer_id
WHERE (inv.sorted_container BETWEEN 0 AND 3 OR inv.sorted_container BETWEEN 10000 AND 10004)
  AND inv.quantity > 0
  AND (inv.item_id BETWEEN 21792 AND 21799 OR inv.item_id BETWEEN 22526 AND 22529 OR inv.item_id BETWEEN 23903 AND 23906
       OR inv.item_id BETWEEN 24344 AND 24367)
UNION ALL
SELECT NULL, fc.fc_id, 0, NULL, NULL, fc.sorted_container, fc.sorted_slot_index, fc.item_id, fc.quantity
FROM companion_fc_inventory_snapshot fc
WHERE fc.quantity > 0
  AND (fc.item_id BETWEEN 21792 AND 21799 OR fc.item_id BETWEEN 22526 AND 22529 OR fc.item_id BETWEEN 23903 AND 23906
       OR fc.item_id BETWEEN 24344 AND 24367);

-- ---- Gil and the market (db/views.sql has the details) ----

CREATE VIEW fleet_market_settings_view AS
SELECT coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
                 WHERE setting_key = 'market_stale_days' AND setting_value = round(setting_value) AND setting_value BETWEEN 1 AND 90), 7) AS stale_days,
       coalesce((SELECT setting_value = 'true' FROM tier3_settings WHERE setting_key = 'universalis_enabled'), 0) AS live_prices;

CREATE VIEW fleet_market_accounts_view AS
WITH chars AS (
  SELECT cid, coalesce(nullif(account_label, ''), 'Ungrouped') AS label, coalesce(gil, 0) AS gil FROM companion_character_current
),
rets AS (
  SELECT c.label, coalesce(r.gil, 0) AS gil FROM companion_retainer_lookup r JOIN chars c ON c.cid = r.owner_cid
)
SELECT a.label AS account_label, a.characters, coalesce(b.retainers, 0) AS retainers, a.character_gil, coalesce(b.retainer_gil, 0) AS retainer_gil,
       a.character_gil + coalesce(b.retainer_gil, 0) AS total
FROM (SELECT label, count(*) AS characters, sum(gil) AS character_gil FROM chars GROUP BY label) a
LEFT JOIN (SELECT label, count(*) AS retainers, sum(gil) AS retainer_gil FROM rets GROUP BY label) b ON b.label = a.label;

CREATE VIEW fleet_market_listings_view AS
WITH ages AS (
  SELECT l.*, CASE WHEN l.first_seen_at > ((julianday('now') - 2440587.5) * 86400.0) THEN 0
                   ELSE (((julianday('now') - 2440587.5) * 86400.0) - l.first_seen_at) / 86400.0 END AS age_days
  FROM companion_market_listing l
)
SELECT l.retainer_id, l.owner_cid, l.slot, l.item_id, l.quantity, l.hq, l.unit_price, l.first_seen_at,
       r.name AS retainer_name, c.name AS owner_name, c.world, c.account_label, so.sort_index,
       l.unit_price * l.quantity AS value, l.age_days, l.age_days >= st.stale_days AS stale
FROM ages l
LEFT JOIN companion_retainer_lookup r ON r.retainer_id = l.retainer_id
LEFT JOIN companion_character_current c ON c.cid = l.owner_cid
LEFT JOIN character_sort_order so ON so.cid = l.owner_cid
CROSS JOIN fleet_market_settings_view st;

-- Routes (the plugin's routes stage).

CREATE VIEW fleet_route_subs_view AS
SELECT s.cid, s.sub_name,
       coalesce(s.slot, CASE WHEN s.sub_name GLOB 'Submersible-[1-4]' THEN CAST(substr(s.sub_name, 13, 1) AS INTEGER) END) AS slot,
       s.level AS rank, s.part1, s.part2, s.part3, s.part4, s.points, s.return_time, s.current_exp, s.next_level_exp,
       coalesce(t.craft_enabled, 0) AS craft_enabled, CAST(c.fc_id AS TEXT) AS fc_id
FROM companion_submarine_snapshot s
LEFT JOIN companion_character_current c ON c.cid = s.cid
LEFT JOIN sub_craft_toggle t ON t.cid = s.cid AND t.sub_name = s.sub_name
WHERE s.level > 0;

CREATE VIEW fleet_route_fight_club_view AS
SELECT f.id, f.route, f.hours, f.repair_times, f.gil_sub_day, f.rec_build_notes, f.unmod_min_rank, f.min_rank_build,
       coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'repair_kit_cost'), 0) AS repair_kit_cost
FROM fight_club_routes f;

CREATE VIEW fleet_sub_history_view AS
WITH series AS (
  SELECT DISTINCT s.cid, m.metric
  FROM companion_submarine_snapshot s
  CROSS JOIN (SELECT 'sub_rank:1' AS metric UNION ALL SELECT 'sub_rank:2' UNION ALL SELECT 'sub_rank:3' UNION ALL SELECT 'sub_rank:4'
              UNION ALL SELECT 'sub_exp:1' UNION ALL SELECT 'sub_exp:2' UNION ALL SELECT 'sub_exp:3' UNION ALL SELECT 'sub_exp:4') m
  WHERE s.level > 0
),
inside AS (
  SELECT se.cid, se.metric, h.recorded_at, h.value,
         lead(h.value) OVER (PARTITION BY se.cid, se.metric ORDER BY h.recorded_at DESC) AS earlier,
         row_number() OVER (PARTITION BY se.cid, se.metric ORDER BY h.recorded_at DESC) AS n
  FROM series se
  JOIN companion_metric_history h ON h.subject_type = 'character' AND h.subject_id = +se.cid AND h.metric = se.metric
   AND h.recorded_at > ((julianday('now') - 2440587.5) * 86400.0) - 14 * 86400.0
)
SELECT cid, metric, recorded_at, value FROM inside WHERE n = 1 OR earlier IS NULL OR earlier <> value
UNION ALL
SELECT cid, metric, recorded_at, value FROM (
  SELECT se.cid, se.metric,
         (SELECT h.recorded_at FROM companion_metric_history h
          WHERE h.subject_type = 'character' AND h.subject_id = +se.cid AND h.metric = se.metric
            AND h.recorded_at <= ((julianday('now') - 2440587.5) * 86400.0) - 14 * 86400.0 ORDER BY h.recorded_at DESC LIMIT 1) AS recorded_at,
         (SELECT h.value FROM companion_metric_history h
          WHERE h.subject_type = 'character' AND h.subject_id = +se.cid AND h.metric = se.metric
            AND h.recorded_at <= ((julianday('now') - 2440587.5) * 86400.0) - 14 * 86400.0 ORDER BY h.recorded_at DESC LIMIT 1) AS value
  FROM series se) b
WHERE b.recorded_at IS NOT NULL;

CREATE VIEW fleet_voyage_sectors_view AS
SELECT CAST(v.fc_id AS TEXT) AS fc_id, v.sub_register, v.sub_name, v.returned_at, v.sector, v.leg
FROM companion_voyage_loot v
WHERE v.returned_at >= ((julianday('now') - 2440587.5) * 86400.0) - 14 * 86400.0;

CREATE VIEW fleet_voyage_loot_view AS
WITH settings AS (
  SELECT coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
                   WHERE setting_key = 'income_window_days' AND setting_value IN (3, 7, 14, 30)), 7) AS window_days
)
SELECT CAST(l.fc_id AS TEXT) AS fc_id, l.sub_register, l.returned_at, l.sector, l.sub_name, l.primary_item, l.primary_count,
       l.additional_item, l.additional_count, l.leg, st.window_days
FROM companion_voyage_loot l
CROSS JOIN settings st
WHERE l.returned_at > ((julianday('now') - 2440587.5) * 86400.0) - st.window_days * 86400.0;

CREATE VIEW fleet_rankup_forecast_view AS
SELECT f.cid, f.slot, f.rank, f.made_at, f.predicted_at, f.sub_name, CAST(c.fc_id AS TEXT) AS fc_id, c.name AS character_name
FROM companion_rankup_forecast f
LEFT JOIN companion_character_current c ON c.cid = f.cid
WHERE f.made_at >= ((julianday('now') - 2440587.5) * 86400.0) - 30 * 86400.0;

CREATE VIEW fleet_rankup_history_view AS
WITH series AS (
  SELECT DISTINCT f.cid, 'sub_rank:' || f.slot AS metric
  FROM companion_rankup_forecast f
  WHERE f.made_at >= ((julianday('now') - 2440587.5) * 86400.0) - 30 * 86400.0
),
inside AS (
  SELECT se.cid, se.metric, h.recorded_at, h.value,
         lead(h.value) OVER (PARTITION BY se.cid, se.metric ORDER BY h.recorded_at DESC) AS earlier,
         row_number() OVER (PARTITION BY se.cid, se.metric ORDER BY h.recorded_at DESC) AS n
  FROM series se
  JOIN companion_metric_history h ON h.subject_type = 'character' AND h.subject_id = +se.cid AND h.metric = se.metric
   AND h.recorded_at > ((julianday('now') - 2440587.5) * 86400.0) - 31 * 86400.0
)
SELECT cid, metric, recorded_at, value FROM inside WHERE n = 1 OR earlier IS NULL OR earlier <> value
UNION ALL
SELECT cid, metric, recorded_at, value FROM (
  SELECT se.cid, se.metric,
         (SELECT h.recorded_at FROM companion_metric_history h
          WHERE h.subject_type = 'character' AND h.subject_id = +se.cid AND h.metric = se.metric
            AND h.recorded_at <= ((julianday('now') - 2440587.5) * 86400.0) - 31 * 86400.0 ORDER BY h.recorded_at DESC LIMIT 1) AS recorded_at,
         (SELECT h.value FROM companion_metric_history h
          WHERE h.subject_type = 'character' AND h.subject_id = +se.cid AND h.metric = se.metric
            AND h.recorded_at <= ((julianday('now') - 2440587.5) * 86400.0) - 31 * 86400.0 ORDER BY h.recorded_at DESC LIMIT 1) AS value
  FROM series se) b
WHERE b.recorded_at IS NOT NULL;

CREATE VIEW fleet_voyage_returns_view AS
SELECT DISTINCT CAST(v.fc_id AS TEXT) AS fc_id, v.sub_name, v.returned_at
FROM companion_voyage_loot v
WHERE v.returned_at >= ((julianday('now') - 2440587.5) * 86400.0) - 31 * 86400.0 AND v.sub_name IS NOT NULL;

-- Shopping List (fleet_shopping_held_view and fleet_shopping_progress_view in db/views.sql).
CREATE VIEW fleet_shopping_held_view AS
SELECT item_id, who, place, sum(quantity) AS qty
FROM (
  SELECT h.item_id, h.quantity,
         CASE WHEN h.kind = 'fc' THEN h.who
              WHEN nullif(h.owner_name, '') IS NULL THEN 'Character ' || h.owner_cid
              ELSE h.owner_name || CASE WHEN nullif(h.world, '') IS NULL THEN '' ELSE ' (' || h.world || ')' END END AS who,
         CASE WHEN h.kind = 'fc' THEN 'FC chest'
              WHEN h.kind = 'retainer' THEN 'Retainer ' || coalesce(nullif(h.retainer_name, ''), 'unknown')
              WHEN h.container BETWEEN 0 AND 3 THEN 'Bags'
              WHEN h.container = 2001 THEN 'Crystals'
              ELSE 'Saddlebag' END AS place
  FROM fleet_holdings_view h
  WHERE (h.kind = 'character' AND (h.container BETWEEN 0 AND 3 OR h.container IN (2001, 4000, 4001, 4100, 4101)))
     OR (h.kind = 'retainer' AND (h.container BETWEEN 10000 AND 10006 OR h.container = 12001))
     OR (h.kind = 'fc' AND (h.container BETWEEN 20000 AND 20010 OR h.container = 22001))
) held
GROUP BY item_id, who, place;

CREATE VIEW fleet_shopping_progress_view AS
SELECT setting_value AS progress FROM tier3_settings WHERE setting_key = 'shopping_progress';

-- ---- The shared settings (db/views.sql has the details) ----

CREATE VIEW fleet_settings_view AS
SELECT coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_repair_kit_threshold'), 0) AS repair_kit_threshold,
       coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_ceruleum_threshold'), 0) AS ceruleum_threshold,
       coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
                 WHERE setting_key = 'house_warn_days' AND setting_value = round(setting_value) AND setting_value BETWEEN 1 AND 44), 7) AS house_warn_days,
       coalesce((SELECT CAST(setting_value AS INTEGER) FROM fleet_economics_settings
                 WHERE setting_key = 'not_synced_after_minutes' AND setting_value = round(setting_value) AND setting_value BETWEEN 10 AND 1440), 30) AS not_synced_minutes,
       (SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'repair_kit_cost') AS repair_kit_cost,
       coalesce((SELECT setting_value = 'true' FROM tier3_settings WHERE setting_key = 'universalis_enabled'), 0) AS live_prices,
       coalesce((SELECT setting_value FROM tier3_settings WHERE setting_key = 'play_style' AND setting_value IN ('auto', 'both', 'subs', 'retainers')), 'auto') AS play_style_choice,
       (SELECT count(*) FROM companion_submarine_snapshot s WHERE s.level > 0 AND s.cid IN (SELECT cid FROM companion_character_current)) AS subs_built,
       (SELECT count(*) FROM companion_retainer_lookup r WHERE r.job > 0 AND r.owner_cid IN (SELECT cid FROM companion_character_current)) AS retainers_with_class;
