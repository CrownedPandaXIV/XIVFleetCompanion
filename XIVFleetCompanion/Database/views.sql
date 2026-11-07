-- The views plugin 2.0 reads (and, later in 2.x, the app too): ready-made views of the fleet, so the plugin can show
-- the Summary, Subs and Supplies in the game without the app running. Each does its working out inside the database
-- and returns only the rows a window shows, with the same numbers as the app's tabs (tests/db/verify-fleet-views.js).
--
-- Written with plugin-only use in mind (a one-file SQLite version later in 2.x): views added from 2.x's "rest of the
-- tabs" stage keep to plain SQL where it gives the same answer, so they translate easily. Each has a SQLite twin in
-- db/sqlite/views.sql: change both together (tests/db/verify-sqlite-views.js checks they match row for row). And no view here reads the
-- game data tables (ffxiv_*): a new database has none until the app fills them, and a view that needed them couldn't
-- be made. Names of jobs, ventures and items come from the game's own files in the plugin instead.
--
-- Unlike db/updates/, this file is applied again whenever it changes: after the numbered updates, the app compares
-- its checksum with the one recorded in companion_views and, when they differ, replaces every view here in one
-- transaction (db/apply-updates.js ensureViews). So edit this file freely; a newer app's views are never replaced by
-- an older app's. Views and functions only, never tables or data: nothing is stored here, and nothing else depends
-- on these views, so they are dropped and made again.

DROP VIEW IF EXISTS fleet_settings_view, fleet_shopping_held_view, fleet_shopping_progress_view, fleet_rankup_forecast_view, fleet_rankup_history_view, fleet_voyage_returns_view, fleet_voyage_loot_view, fleet_route_subs_view, fleet_route_fight_club_view, fleet_sub_history_view, fleet_voyage_sectors_view,
  fleet_market_settings_view, fleet_market_accounts_view, fleet_market_listings_view,
  fleet_parts_characters_view, fleet_parts_subs_view, fleet_parts_rules_view, fleet_parts_fight_club_view, fleet_parts_spares_view,
  fleet_venture_income_view, fleet_income_view, fleet_salvage_view, fleet_item_totals_view, fleet_holdings_view, fleet_fcs_view, fleet_pcs_view, fleet_retainer_gear_view, fleet_retainers_view, fleet_roster_view,
  fleet_alerts_view, fleet_summary_view, fleet_retainer_counts_view, fleet_not_synced_view, fleet_subs_view, fleet_supplies_view CASCADE;

-- A sub part's class letter, from its item number (as subs/submarine-builds.js SUB_PART_CLASS; the test checks
-- they match). Anything else shows as its number.
CREATE OR REPLACE FUNCTION fleet_part_letter(item int) RETURNS text LANGUAGE sql IMMUTABLE AS $$
  SELECT CASE
    WHEN item BETWEEN 21792 AND 21795 THEN 'S'
    WHEN item BETWEEN 21796 AND 21799 THEN 'U'
    WHEN item BETWEEN 22526 AND 22529 THEN 'W'
    WHEN item BETWEEN 23903 AND 23906 THEN 'C'
    WHEN item BETWEEN 24344 AND 24347 THEN 'Y'
    WHEN item BETWEEN 24348 AND 24351 THEN 'S+'
    WHEN item BETWEEN 24352 AND 24355 THEN 'U+'
    WHEN item BETWEEN 24356 AND 24359 THEN 'W+'
    WHEN item BETWEEN 24360 AND 24363 THEN 'C+'
    WHEN item BETWEEN 24364 AND 24367 THEN 'Y+'
    ELSE coalesce(item::text, 'null')
  END
$$;

-- Every character with subs: ceruleum and repair kits held, used per day, days left and how many to buy (as the
-- Supplies tab, with its window and restock days from Settings).
--
-- Use per day is worked out as subs/supply-runway.js usageRate does: every drop in the recorded values
-- (companion_metric_history) from the window start to the character's last sync counts as use, a rise is a restock
-- and is ignored, divided by the days from the window start (or its first value, when later) to that sync. The last
-- value recorded at or before the window start is the starting point; for what's in the bags, the value at the last
-- sync counts too. It comes from what's held in bags and retainers together (item_qty:...) when that covers a day,
-- otherwise from the bags. Each character's history is read along its index, so nothing has to be sorted.
--
-- per_day NULL: not enough history yet; per_day 0: none used, so days_left is NULL (they never run out).
CREATE OR REPLACE VIEW fleet_supplies_view AS
WITH settings AS (
  SELECT window_days, target_days, now() - make_interval(days => window_days) AS window_start
  FROM (SELECT
    coalesce((SELECT setting_value::int FROM fleet_economics_settings
              WHERE setting_key = 'supply_window_days' AND setting_value IN (3, 7, 14, 30)), 14) AS window_days,
    coalesce((SELECT round(setting_value)::int FROM fleet_economics_settings
              WHERE setting_key = 'restock_target_days' AND setting_value BETWEEN 1 AND 90), 14) AS target_days) chosen
),
characters AS (
  SELECT c.cid, c.name, c.world, c.account_label, c.fc_id, so.sort_index, c.last_synced_at AS as_of,
         coalesce(c.ceruleum, 0) AS ceruleum, coalesce(c.repair_kits, 0) AS repair_kits
  FROM companion_character_current c
  LEFT JOIN character_sort_order so ON so.cid = c.cid
  WHERE EXISTS (SELECT 1 FROM companion_submarine_snapshot s WHERE s.cid = c.cid AND s.level > 0)
),
series AS (
  SELECT row_number() OVER ()::int AS id, ch.cid, ch.as_of, m.supply, m.metric, m.held,
         CASE WHEN NOT m.held THEN CASE m.supply WHEN 'ceruleum' THEN ch.ceruleum ELSE ch.repair_kits END END AS now_value
  FROM characters ch
  CROSS JOIN (VALUES ('ceruleum', 'item_qty:10155', true), ('ceruleum', 'ceruleum', false),
                     ('repair_kits', 'item_qty:10373', true), ('repair_kits', 'repair_kits', false)) m(supply, metric, held)
),
-- Each series in one pass along its index (newest first, so nothing is sorted): the drops between the values
-- recorded in the window, plus the drop from the starting point to the oldest of them and from the newest of them
-- (or the starting point) to the bags' value at the last sync.
used AS (
  SELECT se.id, base.at AS base_at, base.value AS base_value, w.drops, w.oldest_at, w.oldest_value, w.newest_value,
         se.now_value, se.as_of, st.window_start
  FROM series se CROSS JOIN settings st
  -- the starting point: the last value at or before the window start (and the last sync)
  LEFT JOIN LATERAL (
    SELECT h.recorded_at AS at, h.value FROM companion_metric_history h
    WHERE h.subject_type = 'character' AND h.subject_id = se.cid AND h.metric = se.metric
      AND h.recorded_at <= least(st.window_start, se.as_of)
    ORDER BY h.recorded_at DESC LIMIT 1) base ON true
  -- everything recorded in the window, up to the last sync
  CROSS JOIN LATERAL (
    SELECT coalesce(sum(greatest(older - value, 0)), 0) AS drops,
           min(at) AS oldest_at,
           max(value) FILTER (WHERE older IS NULL) AS oldest_value,  -- the last one read has nothing older
           max(value) FILTER (WHERE newest) AS newest_value          -- the first one read
    FROM (
      SELECT h.recorded_at AS at, h.value,
             lead(h.value) OVER newest_first AS older,
             row_number() OVER newest_first = 1 AS newest
      FROM companion_metric_history h
      WHERE h.subject_type = 'character' AND h.subject_id = se.cid AND h.metric = se.metric
        AND h.recorded_at > st.window_start AND h.recorded_at <= se.as_of
      WINDOW newest_first AS (ORDER BY h.recorded_at DESC)) inside) w
),
totals AS (
  SELECT id, as_of, window_start,
         coalesce(base_at, oldest_at, CASE WHEN now_value IS NOT NULL THEN as_of END) AS first_at,
         drops
         + CASE WHEN base_value IS NOT NULL AND oldest_value IS NOT NULL THEN greatest(base_value - oldest_value, 0) ELSE 0 END
         + CASE WHEN now_value IS NOT NULL AND as_of > window_start AND coalesce(newest_value, base_value) IS NOT NULL
                THEN greatest(coalesce(newest_value, base_value) - now_value, 0) ELSE 0 END AS used
  FROM used
),
rates AS (
  SELECT se.cid, se.supply, se.held,
         CASE WHEN t.first_at IS NOT NULL  -- no values at all: not enough history
                   AND extract(epoch FROM (t.as_of - greatest(t.window_start, t.first_at))) / 86400 >= 1
              THEN t.used::double precision / (extract(epoch FROM (t.as_of - greatest(t.window_start, t.first_at))) / 86400) END AS per_day
  FROM series se JOIN totals t ON t.id = se.id
),
per_day AS (
  SELECT ch.*,
         coalesce((SELECT per_day FROM rates r WHERE r.cid = ch.cid AND r.supply = 'ceruleum' AND r.held),
                  (SELECT per_day FROM rates r WHERE r.cid = ch.cid AND r.supply = 'ceruleum' AND NOT r.held)) AS ceruleum_per_day,
         coalesce((SELECT per_day FROM rates r WHERE r.cid = ch.cid AND r.supply = 'repair_kits' AND r.held),
                  (SELECT per_day FROM rates r WHERE r.cid = ch.cid AND r.supply = 'repair_kits' AND NOT r.held)) AS repair_kits_per_day
  FROM characters ch
)
SELECT p.cid, p.name, p.world, p.account_label, p.fc_id, p.sort_index, p.as_of AS last_synced_at,
       p.ceruleum, p.ceruleum_per_day,
       CASE WHEN p.ceruleum_per_day > 0 THEN greatest(p.ceruleum, 0) / p.ceruleum_per_day END AS ceruleum_days_left,
       CASE WHEN p.ceruleum_per_day > 0 THEN greatest(0, ceil(p.ceruleum_per_day * st.target_days - p.ceruleum))::int ELSE 0 END AS ceruleum_restock,
       p.repair_kits, p.repair_kits_per_day,
       CASE WHEN p.repair_kits_per_day > 0 THEN greatest(p.repair_kits, 0) / p.repair_kits_per_day END AS repair_kits_days_left,
       CASE WHEN p.repair_kits_per_day > 0 THEN greatest(0, ceil(p.repair_kits_per_day * st.target_days - p.repair_kits))::int ELSE 0 END AS repair_kits_restock,
       st.window_days, st.target_days
FROM per_day p CROSS JOIN settings st;

-- Every built sub, with its character, rank, parts (as the Subs tab's Current Setup, for example "S-S-U-C") and
-- where it is: 'in_port' (no voyage), 'returned' (back, waiting to be collected) or 'out' (back at return_time,
-- Unix seconds).
CREATE OR REPLACE VIEW fleet_subs_view AS
SELECT s.cid, c.name AS character_name, c.world, c.account_label, c.fc_id, so.sort_index,
       s.sub_name, coalesce(s.slot, substring(s.sub_name FROM '^Submersible-([1-4])$')::int) AS slot,
       s.level AS rank,
       fleet_part_letter(s.part1) || '-' || fleet_part_letter(s.part2) || '-' || fleet_part_letter(s.part3) || '-' || fleet_part_letter(s.part4) AS build,
       s.return_time,
       CASE WHEN coalesce(s.return_time, 0) <= 0 THEN 'in_port'
            WHEN s.return_time <= extract(epoch FROM now()) THEN 'returned'
            ELSE 'out' END AS status,
       s.current_exp, s.next_level_exp, s.updated_at
FROM companion_submarine_snapshot s
LEFT JOIN companion_character_current c ON c.cid = s.cid
LEFT JOIN character_sort_order so ON so.cid = s.cid
WHERE s.level > 0;

-- Characters the plugin no longer syncs: unticked or gone from AutoRetainer, so their last data stays and falls behind
-- the rest of their account (as characters/not-synced.js). Behind by at least Settings → General → Warnings' "not
-- synced" minutes (10 to 1440, 30 by default). Their supplies are left out of the low-supply warnings, as in the app.
CREATE OR REPLACE VIEW fleet_not_synced_view AS
WITH setting AS (
  SELECT coalesce((SELECT setting_value::int FROM fleet_economics_settings
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
WHERE a.newest - c.last_synced_at >= make_interval(mins => st.minutes);

-- The retainers with a class right now, as the app's Summary (characters/retainer-summary.js): how many there are,
-- ventures done and waiting to be collected, away on one, and without one. Only retainers of characters in the Roster
-- that the plugin still syncs. A venture with no end time counts as away, as there. Small and quick, so the plugin
-- reads it with the subs (every 30 seconds): a retainer caught between two ventures while AutoRetainer collects and
-- sends it again clears at the next read after the next sync.
CREATE OR REPLACE VIEW fleet_retainer_counts_view AS
SELECT CAST(count(*) AS int) AS retainers,
       CAST(count(*) FILTER (WHERE rl.has_venture AND coalesce(rl.venture_ends_at, 0) <> 0 AND rl.venture_ends_at <= extract(epoch FROM now())) AS int) AS ventures_done,
       CAST(count(*) FILTER (WHERE rl.has_venture AND NOT (coalesce(rl.venture_ends_at, 0) <> 0 AND rl.venture_ends_at <= extract(epoch FROM now()))) AS int) AS retainers_away,
       CAST(count(*) FILTER (WHERE NOT coalesce(rl.has_venture, false)) AS int) AS retainers_idle
FROM companion_retainer_lookup rl
JOIN companion_character_current c ON c.cid = rl.owner_cid
WHERE rl.job > 0
  AND rl.owner_cid NOT IN (SELECT cid FROM fleet_not_synced_view);

-- The fleet at a glance, one row: subs back and out and when the next one is back, retainers on and off ventures,
-- characters below the low-supply warnings (Settings → General → Warnings; 0 turns one off; characters no longer
-- synced are left out, as in the app) and the soonest a character runs out of either.
CREATE OR REPLACE VIEW fleet_summary_view AS
WITH thresholds AS (
  SELECT
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_ceruleum_threshold'), 0) AS ceruleum,
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_repair_kit_threshold'), 0) AS repair_kits
),
subs AS (
  SELECT count(*)::int AS subs,
         (count(*) FILTER (WHERE status = 'returned'))::int AS subs_returned,
         (count(*) FILTER (WHERE status = 'in_port'))::int AS subs_in_port,
         (count(*) FILTER (WHERE status = 'out'))::int AS subs_out,
         min(return_time) FILTER (WHERE status = 'out') AS next_return_time
  FROM fleet_subs_view
),
retainers AS (
  SELECT retainers, ventures_done, retainers_idle FROM fleet_retainer_counts_view
),
supplies AS (
  SELECT (count(*) FILTER (WHERE t.ceruleum > 0 AND f.ceruleum < t.ceruleum))::int AS low_ceruleum,
         (count(*) FILTER (WHERE t.repair_kits > 0 AND f.repair_kits < t.repair_kits))::int AS low_repair_kits,
         min(least(f.ceruleum_days_left, f.repair_kits_days_left)) AS soonest_runs_out_days
  FROM fleet_supplies_view f CROSS JOIN thresholds t
  WHERE f.cid NOT IN (SELECT cid FROM fleet_not_synced_view)
)
SELECT subs.*, retainers.*, supplies.* FROM subs, retainers, supplies;

-- What needs attention, one row each, worst first (as the app's Summary warnings): level 'danger' or 'warn', kind,
-- who (subject) and what (detail).
--   house          an FC house nobody has entered for the house warning's days (Settings → General → Warnings, 1 to
--                  44, 7 by default) or more, or with no date yet; 'danger' from 30 days (the game demolishes at 45)
--   low_ceruleum   a character with subs below the low-ceruleum warning (left out when no longer synced)
--   low_repair_kits  the same for repair kits
--   not_synced     a character the plugin no longer syncs (fleet_not_synced_view)
--   autoretainer   a setup of the plugin that synced in the last day but found AutoRetainer not running
-- Subs back and waiting are counted live by the plugin from fleet_subs_view, so they're not here.
CREATE OR REPLACE VIEW fleet_alerts_view AS
WITH settings AS (
  SELECT
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_ceruleum_threshold'), 0) AS ceruleum,
    coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_repair_kit_threshold'), 0) AS repair_kits,
    coalesce((SELECT setting_value::int FROM fleet_economics_settings
              WHERE setting_key = 'house_warn_days' AND setting_value = round(setting_value) AND setting_value BETWEEN 1 AND 44), 7) AS house_days
),
houses AS (
  SELECT DISTINCT ON (fc_id) fc_id, fc_name, has_house
  FROM companion_character_housing WHERE fc_id IS NOT NULL
  ORDER BY fc_id, updated_at DESC
),
visited AS (
  SELECT fc_id, max(house_last_visited) AS last_visited
  FROM companion_character_housing WHERE fc_id IS NOT NULL GROUP BY fc_id
),
house_ages AS (
  SELECT h.fc_id, coalesce(h.fc_name, 'FC ' || h.fc_id) AS fc_name, v.last_visited,
         CASE WHEN v.last_visited IS NOT NULL
              THEN greatest(0, floor(extract(epoch FROM (now() - v.last_visited)) / 86400))::int END AS days
  FROM houses h LEFT JOIN visited v ON v.fc_id = h.fc_id
  WHERE h.has_house
),
supplies AS (
  SELECT c.cid, c.name || ' (' || c.world || ')' AS who, coalesce(c.ceruleum, 0) AS ceruleum, coalesce(c.repair_kits, 0) AS repair_kits
  FROM companion_character_current c
  WHERE EXISTS (SELECT 1 FROM companion_submarine_snapshot s WHERE s.cid = c.cid AND s.level > 0)
    AND c.cid NOT IN (SELECT cid FROM fleet_not_synced_view)
)
SELECT * FROM (
SELECT CASE WHEN days IS NULL OR days < 30 THEN 'warn' ELSE 'danger' END AS level, 'house' AS kind, fc_name AS subject,
       CASE WHEN days IS NULL THEN 'house: no date yet for when it was last entered'
            WHEN days >= 45 THEN 'house not entered for ' || days || ' days: past 45, the game can demolish it any time'
            ELSE 'house not entered for ' || days || ' day' || CASE WHEN days = 1 THEN '' ELSE 's' END
                 || '; demolished after ' || (45 - days) || ' more' END AS detail,
       coalesce(days, 0) AS weight
FROM house_ages, settings st WHERE days IS NULL OR days >= st.house_days
UNION ALL
SELECT 'warn', 'low_ceruleum', s.who, s.ceruleum || ' ceruleum (warning below ' || st.ceruleum::int || ')', 0
FROM supplies s, settings st WHERE st.ceruleum > 0 AND s.ceruleum < st.ceruleum
UNION ALL
SELECT 'warn', 'low_repair_kits', s.who, s.repair_kits || ' repair kits (warning below ' || st.repair_kits::int || ')', 0
FROM supplies s, settings st WHERE st.repair_kits > 0 AND s.repair_kits < st.repair_kits
UNION ALL
SELECT 'warn', 'not_synced', name || ' (' || world || ')',
       'no longer synced (' || floor(extract(epoch FROM behind) / 3600)::int || ' h behind its account): unticked in the plugin, or its AutoRetainer no longer has it', 0
FROM fleet_not_synced_view
UNION ALL
SELECT 'warn', 'autoretainer', pc_name || CASE WHEN account_label <> '' THEN ' (' || account_label || ')' ELSE '' END,
       'AutoRetainer was not running at its last sync, so nothing syncs from there', 0
FROM companion_pc_status WHERE autoretainer_ready = false AND last_sync_at > now() - interval '1 day'
) alerts
-- Ties by who, letter by letter (COLLATE "C"), so the order is the same whatever the database's language setting, and
-- the same as SQLite's.
ORDER BY level, kind, weight DESC, subject COLLATE "C";


-- 2.x, the rest of the tabs.

-- Every character, as the Roster tab: its Free Company and house, gil, supplies, retainers and subs, when it last
-- synced and on which setup it last logged in (only that one syncs it). behind_minutes: how far it is behind its
-- account when the plugin no longer syncs it (fleet_not_synced_view), else NULL. low_ceruleum / low_repair_kits:
-- below the low-supply warnings, for characters with a built sub that are still synced (as ipc/roster.js, through
-- characters/roster-warnings.js). num_sub_slots: the sub slots unlocked; ceruleum_warning / repair_kit_warning: the
-- warnings themselves (0: off), so the game can colour how close a character is.
CREATE OR REPLACE VIEW fleet_roster_view AS
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
       CAST(coalesce(b.subs_built, 0) AS int) AS subs_built,
       CAST(floor(extract(epoch FROM ns.behind) / 60) AS int) AS behind_minutes,
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

-- Every retainer, as the Retainers tab: its owner, class (job, a ClassJob number) and level, gil, venture (venture_id,
-- a RetainerTask number; venture_ends_at in Unix seconds), gear target, and whether Allagan Tools has seen its items
-- (they're stored, or it was seen holding nothing). The plugin names the class and venture from the game's files.
CREATE OR REPLACE VIEW fleet_retainers_view AS
SELECT rl.retainer_id, rl.owner_cid, c.name AS owner_name, c.world, c.account_label, so.sort_index,
       rl.name AS retainer_name, rl.job, rl.level, rl.gil, rl.has_venture, rl.venture_id, rl.venture_ends_at, rl.hire_order_index,
       t.target_mode, t.target_value, coalesce(t.only_crafted, false) AS only_crafted,
       (rl.items_seen_at IS NOT NULL OR EXISTS (SELECT 1 FROM companion_inventory_snapshot inv
                                                WHERE inv.owner_cid = rl.owner_cid AND inv.retainer_id = rl.retainer_id)) AS items_seen
FROM companion_retainer_lookup rl
LEFT JOIN companion_character_current c ON c.cid = rl.owner_cid
LEFT JOIN retainer_gear_targets t ON t.retainer_id = rl.retainer_id
LEFT JOIN character_sort_order so ON so.cid = rl.owner_cid;

-- What each retainer wears (its equipped items, container 11000), one row per item, for its average item level. The
-- plugin looks up each item's level in the game's files, leaving out any it doesn't know, as the app does.
CREATE OR REPLACE VIEW fleet_retainer_gear_view AS
SELECT retainer_id, item_id FROM companion_inventory_snapshot WHERE sorted_container = 11000 AND item_id IS NOT NULL;

-- Every setup of the plugin (PC and account label), as the PCs tab: its version, when it last synced, which plugins it
-- found, its characters, what it couldn't see (problems, one per line) and its last "Check what I can see".
CREATE OR REPLACE VIEW fleet_pcs_view AS
SELECT pc_name, account_label, plugin_version, last_sync_at, autoretainer_ready, allagantools_ready, fctracker_found,
       submarinetracker_found, venture_stats_found, characters_synced, characters_left_out, characters_elsewhere,
       array_to_string(problems, E'\n') AS problems, check_text, check_to_fix, check_at, last_read_at
FROM companion_pc_status;

-- Every Free Company, as the Free Companies tab (characters/fc-directory-rows.js): its newest details (from whichever
-- member's row was updated last), leader, points, rank, members, house, when a member last entered it and how that
-- compares with the house warning (house_level: 'ok', 'warn', 'danger' from 30 days, 'unknown' with no date; NULL
-- without a house), and, without a house, when it can bid (eligibility: 'Eligible', 'Eligible in N days' or 'Rank too
-- low (N/6)', 30 days after founding or FC Tracker's override date, at rank 6). home_world_id is a World number the
-- plugin names. account_label: the first of its members' account labels; sort_index: its first member in the Roster.
CREATE OR REPLACE VIEW fleet_fcs_view AS
WITH settings AS (
  SELECT coalesce((SELECT CAST(setting_value AS int) FROM fleet_economics_settings
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
         CAST(substring(n.fc_rank FROM '^\s*([0-9]+)') AS int) AS rank_number,
         CASE WHEN n.has_house AND v.house_last_visited IS NOT NULL
              THEN CAST(greatest(0, floor(extract(epoch FROM (now() - v.house_last_visited)) / 86400)) AS int) END AS house_days
  FROM newest n
  LEFT JOIN visited v ON v.fc_id = n.fc_id
  LEFT JOIN housing_key_city_lookup k ON k.city_id = n.house_city
  LEFT JOIN members m ON m.fc_id = n.fc_id
),
ages AS (
  SELECT f.*, extract(epoch FROM (now() - f.reference)) / 86400 AS days_since_founding FROM fcs f
)
SELECT a.fc_id, a.fc_name, a.fc_master, a.fc_points, a.fc_rank, a.total_members, a.has_house,
       CASE WHEN a.has_house THEN coalesce(a.district_name, 'City ' || a.house_city) || ' W' || a.house_ward || ' P' || a.house_plot END AS house,
       a.home_world_id, a.house_last_visited, a.house_days,
       CASE WHEN NOT a.has_house OR a.has_house IS NULL THEN NULL
            WHEN a.house_days IS NULL THEN 'unknown'
            WHEN a.house_days >= 30 THEN 'danger'
            WHEN a.house_days >= st.house_days THEN 'warn'
            ELSE 'ok' END AS house_level,
       CASE WHEN a.has_house AND a.house_days IS NOT NULL THEN greatest(0, 45 - a.house_days) END AS demolition_in_days,
       CASE WHEN NOT coalesce(a.has_house, false) AND a.reference IS NOT NULL THEN a.reference + interval '30 days' END AS eligible_at,
       CASE WHEN coalesce(a.has_house, false) OR a.reference IS NULL THEN NULL
            WHEN a.rank_number >= 6 AND a.days_since_founding >= 30 THEN 'Eligible'
            WHEN a.days_since_founding < 30 THEN 'Eligible in ' || CAST(ceil(30 - a.days_since_founding) AS int) || ' day'
                 || CASE WHEN ceil(30 - a.days_since_founding) = 1 THEN '' ELSE 's' END
            ELSE 'Rank too low (' || coalesce(CAST(a.rank_number AS text), '?') || '/6)' END AS eligibility,
       a.account_label, a.sort_index
FROM ages a CROSS JOIN settings st;

-- Every stack of every item the fleet holds, one row each, for the in-game Inventory (characters, Free Companies, the
-- whole fleet, and Where is it?). kind: 'character' (its own bags and so on), 'retainer' or 'fc' (a Free Company
-- chest). item_id is the plain item, with hq for a high-quality one (the game stores those 1,000,000 higher).
-- place says where in plain words, as Where is it? (characters/item-locations.js placeLabel); container_name is the
-- app's own name for the container. money: a container that holds gil, not items. For a Free Company chest, owner_cid,
-- owner_name, account_label and sort_index are those of the member the chest is counted on (the leader when tracked,
-- else the first in the Roster: characters/fc-chest.js chestHolders), so it sorts with that character.
CREATE OR REPLACE VIEW fleet_holdings_view AS
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
                      so.sort_index NULLS LAST, c.name COLLATE "C", CAST(c.cid AS text) COLLATE "C") AS rn
    FROM companion_character_current c
    LEFT JOIN character_sort_order so ON so.cid = c.cid
    LEFT JOIN companion_character_housing h ON h.cid = c.cid AND h.fc_id = c.fc_id
    WHERE c.fc_id IS NOT NULL AND c.fc_id <> 0) ranked
  WHERE rn = 1
),
stacks AS (
  SELECT CASE WHEN inv.retainer_id = inv.owner_cid THEN 'character' ELSE 'retainer' END AS kind,
         inv.owner_cid, inv.retainer_id, CAST(NULL AS numeric) AS fc_id, inv.sorted_container AS container, inv.item_id, inv.quantity,
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
       -- Gear being worn: by the character (1000, 1001) or by a retainer (11000), for "Hide equipped gear".
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
                ELSE ' (' || coalesce(nullif(container_name, ''), CAST(container AS text)) || ')' END
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
                WHEN container >= 25000 AND container < 28000 THEN 'House: ' || coalesce(nullif(container_name, ''), CAST(container AS text))
                ELSE coalesce(nullif(container_name, ''), 'Container ' || container) END
       END AS place
FROM stacks;

-- How many of each item the whole fleet holds, as the Inventory tab's Fleet totals: every character's and retainer's
-- stacks except the character's gil, plus everything in the Free Company chests. A high-quality item counts apart
-- from the normal one (hq), as there. equipped: how many of them are being worn (total - equipped with "Hide equipped
-- gear" on).
CREATE OR REPLACE VIEW fleet_item_totals_view AS
SELECT item_id, hq, sum(quantity) AS total, sum(CASE WHEN equipped THEN quantity ELSE 0 END) AS equipped
FROM fleet_holdings_view
WHERE NOT (kind <> 'fc' AND container = 2000)
GROUP BY item_id, hq;

-- Salvage held by each character, as the Salvage tab (ipc/inventory.js get-salvage): every piece in its bags,
-- retainers and so on, plus its Free Company chest's when the chest is counted on it (the leader when tracked, else
-- the first in the Roster, as fleet_holdings_view), at the fixed vendor prices. Standard: items 22500 to 22503;
-- extravagant: 22504 to 22507. One row per character holding any, with how many of each piece (q22500 and so on).
CREATE OR REPLACE VIEW fleet_salvage_view AS
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
       CAST(sum(CASE WHEN h.item_id <= 22503 THEN h.quantity ELSE 0 END) AS bigint) AS standard,
       CAST(sum(CASE WHEN h.item_id > 22503 THEN h.quantity ELSE 0 END) AS bigint) AS extravagant,
       CAST(sum(h.quantity * p.gil) AS bigint) AS gil,
       CAST(sum(CASE WHEN h.item_id = 22500 THEN h.quantity ELSE 0 END) AS bigint) AS q22500,
       CAST(sum(CASE WHEN h.item_id = 22501 THEN h.quantity ELSE 0 END) AS bigint) AS q22501,
       CAST(sum(CASE WHEN h.item_id = 22502 THEN h.quantity ELSE 0 END) AS bigint) AS q22502,
       CAST(sum(CASE WHEN h.item_id = 22503 THEN h.quantity ELSE 0 END) AS bigint) AS q22503,
       CAST(sum(CASE WHEN h.item_id = 22504 THEN h.quantity ELSE 0 END) AS bigint) AS q22504,
       CAST(sum(CASE WHEN h.item_id = 22505 THEN h.quantity ELSE 0 END) AS bigint) AS q22505,
       CAST(sum(CASE WHEN h.item_id = 22506 THEN h.quantity ELSE 0 END) AS bigint) AS q22506,
       CAST(sum(CASE WHEN h.item_id = 22507 THEN h.quantity ELSE 0 END) AS bigint) AS q22507
FROM held h
JOIN prices p ON p.item_id = h.item_id
JOIN companion_character_current c ON c.cid = h.cid
LEFT JOIN character_sort_order so ON so.cid = c.cid
GROUP BY c.cid, c.name, c.world, c.account_label, so.sort_index;

-- Each character's real net income from its subs, as the Income tab (ipc/income.js and subs/income.js
-- characterIncome; the Fight Club "expected" columns come later in 2.x): salvage that came in over the window (from
-- Settings, 7 days by default), at its vendor price, less the repair kits used at the repair kit cost, per day.
--
-- From the values recorded over time (companion_metric_history): the last one at or before the window start and
-- everything after it. Salvage that came in: every rise in the count of a piece, except a rise at the same moment
-- as items were stored for a new source (inventory_sources rose: first_stored) and the part matched by a drop of the
-- same piece in the character's FC chest at that moment (taken out of the chest: from_chest). Repair kits used:
-- every drop in those held in bags and retainers (item_qty:10373) when that history reaches back to the start, else
-- in the bags. The window runs from its start (or the first salvage value, when later) to the character's last
-- sync (or its newest value, when later); less than a day of it is not enough (enough false, the numbers NULL).
CREATE OR REPLACE VIEW fleet_income_view AS
WITH settings AS (
  SELECT window_days, kit_cost, now() - make_interval(days => window_days) AS window_start
  FROM (SELECT
    coalesce((SELECT CAST(setting_value AS int) FROM fleet_economics_settings
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
-- Each series from the last value at or before the window start, with the value before each one.
series AS (
  SELECT subject_type, subject_id, metric, recorded_at AS at, value,
         lag(value) OVER (PARTITION BY subject_type, subject_id, metric ORDER BY recorded_at) AS prev
  FROM (
    SELECT h.subject_type, h.subject_id, h.metric, h.recorded_at, h.value
    FROM companion_metric_history h CROSS JOIN settings st
    WHERE h.recorded_at > st.window_start AND h.metric IN (SELECT metric FROM metrics)
      AND ((h.subject_type = 'character' AND h.subject_id IN (SELECT cid FROM characters))
        OR (h.subject_type = 'fc' AND h.subject_id IN (SELECT fc_id FROM characters)))
    UNION ALL
    SELECT b.subject_type, b.subject_id, b.metric, b.recorded_at, b.value
    FROM (SELECT 'character' AS subject_type, cid AS subject_id FROM characters
          UNION SELECT 'fc', fc_id FROM characters WHERE fc_id IS NOT NULL) s
    CROSS JOIN metrics m CROSS JOIN settings st
    CROSS JOIN LATERAL (
      SELECT h.subject_type, h.subject_id, h.metric, h.recorded_at, h.value FROM companion_metric_history h
      WHERE h.subject_type = s.subject_type AND h.subject_id = s.subject_id AND h.metric = m.metric
        AND h.recorded_at <= st.window_start
      ORDER BY h.recorded_at DESC LIMIT 1) b
  ) points
),
-- Per character: its newest value and its first salvage value.
reach AS (
  SELECT subject_id AS cid, max(at) AS newest_at,
         min(CASE WHEN metric LIKE 'item_qty:225%' THEN at END) AS salvage_first,
         min(CASE WHEN metric = 'item_qty:10373' THEN at END) AS kits_total_first
  FROM series WHERE subject_type = 'character'
  GROUP BY subject_id
),
windows AS (
  SELECT ch.cid, ch.fc_id, r.kits_total_first, t.end_at, t.from_at, extract(epoch FROM (t.end_at - t.from_at)) / 86400 AS span_days
  FROM characters ch CROSS JOIN settings st
  LEFT JOIN reach r ON r.cid = ch.cid
  CROSS JOIN LATERAL (
    SELECT e.end_at, CASE WHEN r.salvage_first IS NOT NULL THEN greatest(st.window_start, r.salvage_first) ELSE e.end_at END AS from_at
    FROM (SELECT coalesce(greatest(ch.last_synced_at, r.newest_at), now()) AS end_at) e) t
),
-- Moments items were stored for a new source, and salvage leaving an FC chest.
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
  SELECT w.cid, CAST(substring(se.metric FROM 10) AS int) AS item_id, se.value - se.prev AS amount,
         ns.cid IS NOT NULL AS first_stored, coalesce(cd.fell, 0) AS chest_drop
  FROM windows w
  JOIN series se ON se.subject_type = 'character' AND se.subject_id = w.cid AND se.metric LIKE 'item_qty:225%'
  LEFT JOIN new_sources ns ON ns.cid = w.cid AND ns.at = se.at
  LEFT JOIN chest_drops cd ON cd.fc_id = w.fc_id AND cd.metric = se.metric AND cd.at = se.at
  WHERE se.at > w.from_at AND se.value > se.prev
),
salvage AS (
  SELECT r.cid,
         sum(CASE WHEN r.first_stored THEN 0 ELSE r.amount - least(r.amount, r.chest_drop) END) AS pieces,
         sum(CASE WHEN r.first_stored THEN 0 ELSE least(r.amount, r.chest_drop) END) AS from_chest,
         sum(CASE WHEN r.first_stored THEN r.amount ELSE 0 END) AS first_stored,
         sum(CASE WHEN r.first_stored THEN 0 ELSE (r.amount - least(r.amount, r.chest_drop)) * p.gil END) AS gil
  FROM rises r JOIN prices p ON p.item_id = r.item_id
  GROUP BY r.cid
),
-- Which repair kit history: those in bags and retainers when it reaches back to the start, else the bags.
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
       CAST(CASE WHEN w.span_days >= 1 THEN (coalesce(sv.gil, 0) - k.used * st.kit_cost) / w.span_days END AS double precision) AS per_day,
       CAST(CASE WHEN w.span_days >= 1 THEN coalesce(sv.gil, 0) / w.span_days END AS double precision) AS salvage_per_day,
       CAST(CASE WHEN w.span_days >= 1 THEN k.used * st.kit_cost / w.span_days END AS double precision) AS kit_per_day,
       st.window_days, st.kit_cost
FROM characters ch
JOIN windows w ON w.cid = ch.cid
JOIN kits k ON k.cid = ch.cid
LEFT JOIN salvage sv ON sv.cid = ch.cid
CROSS JOIN settings st;

-- Venture rewards AutoRetainer recorded over the Income window, as the Income tab's Retainer Ventures: one row per
-- character and item (normal and high quality together), with that character's number of rewards and retainers.
-- Characters no longer in the Roster are left out (their rewards stay recorded).
-- The game's vendor price comes from the game's own files in the plugin; salvage_price is the fixed salvage price
-- (as the app's), NULL for anything else. hq_quantity: how many of quantity are high quality (priced apart with live
-- market prices).
CREATE OR REPLACE VIEW fleet_venture_income_view AS
WITH settings AS (
  SELECT window_days, now() - make_interval(days => window_days) AS window_start
  FROM (SELECT coalesce((SELECT CAST(setting_value AS int) FROM fleet_economics_settings
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

-- ---- The Parts tab's inputs (plugin 2.0 batch 4) ----
-- Parts are worked out in the plugin (PartsPlanner.cs) the same way as the app's (ipc/parts.js computePartsNeeded),
-- not in a view: handing out spares one at a time, with a shared FC chest never promising one part twice, is a
-- step-by-step job a view does badly. These views give the plugin exactly what the app's working out reads, with
-- item numbers rather than names, in a fixed order. tests/db/verify-fleet-views.js writes a made-up fleet's views
-- and the app's answers to a file the plugin's tests check its own answers against.

-- Every character: its place in the Roster, its Free Company (as text: the SQLite version keeps it as text) and
-- how many sub slots its workshop has, and whether its FC has a house (a character with a house but no workshop
-- yet needs its first sub's parts).
CREATE OR REPLACE VIEW fleet_parts_characters_view AS
SELECT c.cid, c.name, c.world, c.account_label, so.sort_index, CAST(c.fc_id AS text) AS fc_id,
       coalesce(c.num_sub_slots, 0) AS num_sub_slots, coalesce(h.has_house, false) AS has_house
FROM companion_character_current c
LEFT JOIN character_sort_order so ON so.cid = c.cid
LEFT JOIN companion_character_housing h ON h.cid = c.cid;

-- Every built sub with its workshop slot (the slot column, else the number in a default name), rank, the four
-- parts' item numbers (hull, stern, bow, bridge) and whether it's ticked Craft? (follows Fight Club's builds).
CREATE OR REPLACE VIEW fleet_parts_subs_view AS
SELECT s.cid, s.sub_name, coalesce(s.slot, substring(s.sub_name FROM '^Submersible-([1-4])$')::int) AS slot,
       s.level AS rank, s.part1, s.part2, s.part3, s.part4,
       coalesce(t.craft_enabled, false) AS craft_enabled
FROM companion_submarine_snapshot s
LEFT JOIN sub_craft_toggle t ON t.cid = s.cid AND t.sub_name = s.sub_name
WHERE s.level > 0;

-- The Sub Rules (the build each sub should have from a rank), in the order they were made.
CREATE OR REPLACE VIEW fleet_parts_rules_view AS
SELECT id, rank, cid_override, sub_filter, hull, stern, bow, bridge FROM sub_build_rules;

-- Fight Club's routes with a recommended build, for subs ticked Craft? from rank 85, with the repair kit price from
-- Settings (0 when not set) for its gil a day after repair kits.
CREATE OR REPLACE VIEW fleet_parts_fight_club_view AS
SELECT f.id, f.route, f.hours, f.repair_times, f.gil_sub_day, f.rec_build_notes,
       coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'repair_kit_cost'), 0) AS repair_kit_cost
FROM fight_club_routes f
WHERE f.rec_build_notes IS NOT NULL AND f.rec_build_notes <> '';

-- Spare sub parts: in each character's bags (containers 0 to 3) and its retainers' (10000 to 10004), with the
-- retainer's number and name; and in each Free Company's chest (cid NULL, fc_id set; its tracked members share it). A
-- character's own stacks come before its retainers', then by container and slot.
CREATE OR REPLACE VIEW fleet_parts_spares_view AS
SELECT inv.owner_cid AS cid, CAST(NULL AS text) AS fc_id, (inv.retainer_id <> inv.owner_cid) AS is_retainer, inv.retainer_id,
       rl.name AS retainer_name, inv.sorted_container, inv.sorted_slot_index, inv.item_id, inv.quantity
FROM companion_inventory_snapshot inv
LEFT JOIN companion_retainer_lookup rl ON rl.retainer_id = inv.retainer_id
WHERE (inv.sorted_container BETWEEN 0 AND 3 OR inv.sorted_container BETWEEN 10000 AND 10004)
  AND inv.quantity > 0
  AND (inv.item_id BETWEEN 21792 AND 21799 OR inv.item_id BETWEEN 22526 AND 22529 OR inv.item_id BETWEEN 23903 AND 23906
       OR inv.item_id BETWEEN 24344 AND 24367)
UNION ALL
SELECT NULL, CAST(fc.fc_id AS text), false, NULL, NULL, fc.sorted_container, fc.sorted_slot_index, fc.item_id, fc.quantity
FROM companion_fc_inventory_snapshot fc
WHERE fc.quantity > 0
  AND (fc.item_id BETWEEN 21792 AND 21799 OR fc.item_id BETWEEN 22526 AND 22529 OR fc.item_id BETWEEN 23903 AND 23906
       OR fc.item_id BETWEEN 24344 AND 24367);

-- ---- Gil and the market (plugin 2.0's Gil & Market page), as the app's Gil & Market tab (ipc/details.js loadMarket,
-- characters/market.js). Live prices from Universalis are asked for by the plugin itself, as the app does, and only
-- when they're on (fleet_market_settings_view). ----

-- The Gil & Market tab's settings: how many days up for sale counts as unsold for a while (1 to 90, 7 by default),
-- and whether Live Market Prices is on (Settings → General; off by default).
CREATE OR REPLACE VIEW fleet_market_settings_view AS
SELECT coalesce((SELECT CAST(setting_value AS int) FROM fleet_economics_settings
                 WHERE setting_key = 'market_stale_days' AND setting_value = round(setting_value) AND setting_value BETWEEN 1 AND 90), 7) AS stale_days,
       coalesce((SELECT setting_value = 'true' FROM tier3_settings WHERE setting_key = 'universalis_enabled'), false) AS live_prices;

-- Gil per account (no account label: "Ungrouped"): its characters' gil and their retainers' (retainers of characters
-- no longer tracked are left out), as characters/market.js gilByAccount.
CREATE OR REPLACE VIEW fleet_market_accounts_view AS
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

-- Every listing the retainers have up for sale: its retainer and character, value (price each × quantity), how many
-- days since the plugin first saw it (a repriced listing keeps its time) and whether that's at least the setting's
-- days (unsold for a while), as characters/market.js listingAges.
CREATE OR REPLACE VIEW fleet_market_listings_view AS
SELECT l.retainer_id, l.owner_cid, l.slot, l.item_id, l.quantity, l.hq, l.unit_price, l.first_seen_at,
       r.name AS retainer_name, c.name AS owner_name, c.world, c.account_label, so.sort_index,
       l.unit_price * l.quantity AS value,
       CASE WHEN l.first_seen_at > now() THEN 0 ELSE extract(epoch FROM now() - l.first_seen_at) / 86400 END AS age_days,
       (CASE WHEN l.first_seen_at > now() THEN 0 ELSE extract(epoch FROM now() - l.first_seen_at) / 86400 END) >= st.stale_days AS stale
FROM companion_market_listing l
LEFT JOIN companion_retainer_lookup r ON r.retainer_id = l.retainer_id
LEFT JOIN companion_character_current c ON c.cid = l.owner_cid
LEFT JOIN character_sort_order so ON so.cid = l.owner_cid
CROSS JOIN fleet_market_settings_view st;

-- ---- Routes (plugin 2.0's routes stage), as the app's Subs tab (ipc/subs.js loadSubmarines and addRankUps): each sub's
-- route, gil a day after repair kits, speed and range, and when it ranks up and changes build. The plugin works these
-- out itself (SubRoutes.cs), the same way, from these views and the game's own files (the sectors, ranks and parts:
-- views here never read the game data tables). ----

-- Every built sub with what its route and dates need: its route's stops (one byte each), when it's back, its
-- experience into this rank, whether it's ticked Craft? and its Free Company (voyages are recorded by FC).
CREATE OR REPLACE VIEW fleet_route_subs_view AS
SELECT s.cid, s.sub_name, coalesce(s.slot, substring(s.sub_name FROM '^Submersible-([1-4])$')::int) AS slot,
       s.level AS rank, s.part1, s.part2, s.part3, s.part4, s.points, s.return_time, s.current_exp, s.next_level_exp,
       coalesce(t.craft_enabled, false) AS craft_enabled, CAST(c.fc_id AS text) AS fc_id
FROM companion_submarine_snapshot s
LEFT JOIN companion_character_current c ON c.cid = s.cid
LEFT JOIN sub_craft_toggle t ON t.cid = s.cid AND t.sub_name = s.sub_name
WHERE s.level > 0;

-- Every Fight Club route (with or without a recommended build: Current Gil/Day matches a sub's route against all of
-- them), with the ranks its builds need and the repair kit price from Settings (0 when not set).
CREATE OR REPLACE VIEW fleet_route_fight_club_view AS
SELECT f.id, f.route, f.hours, f.repair_times, f.gil_sub_day, f.rec_build_notes, f.unmod_min_rank, f.min_rank_build,
       coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'repair_kit_cost'), 0) AS repair_kit_cost
FROM fight_club_routes f;

-- Each built sub's slot rank and experience as recorded over the last 14 days (its bonus is measured from its last few
-- voyages, its rate from the last week), plus the last value before, as db/metric-window.js reads them: a value
-- recorded again unchanged is left out (the first and the newest stay).
CREATE OR REPLACE VIEW fleet_sub_history_view AS
WITH series AS (
  SELECT DISTINCT s.cid, m.metric
  FROM companion_submarine_snapshot s
  CROSS JOIN (VALUES ('sub_rank:1'), ('sub_rank:2'), ('sub_rank:3'), ('sub_rank:4'),
                     ('sub_exp:1'), ('sub_exp:2'), ('sub_exp:3'), ('sub_exp:4')) m(metric)
  WHERE s.level > 0
)
SELECT se.cid, se.metric, w.recorded_at, w.value
FROM series se
CROSS JOIN LATERAL (
  SELECT recorded_at, value FROM (
    SELECT h.recorded_at, h.value, lead(h.value) OVER newest_first AS earlier, row_number() OVER newest_first AS n
    FROM companion_metric_history h
    WHERE h.subject_type = 'character' AND h.subject_id = se.cid AND h.metric = se.metric
      AND h.recorded_at > now() - interval '14 days'
    WINDOW newest_first AS (ORDER BY h.recorded_at DESC)) inside
  WHERE n = 1 OR earlier IS DISTINCT FROM value) w
UNION ALL
SELECT se.cid, se.metric, b.recorded_at, b.value
FROM series se
CROSS JOIN LATERAL (
  SELECT h.recorded_at, h.value FROM companion_metric_history h
  WHERE h.subject_type = 'character' AND h.subject_id = se.cid AND h.metric = se.metric
    AND h.recorded_at <= now() - interval '14 days'
  ORDER BY h.recorded_at DESC LIMIT 1) b;

-- The sectors of each voyage SubmarineTracker recorded over the last 14 days (by FC and its id for the sub), in the
-- order they were run (leg) when known: a sub's bonus compares what each collection gave with its voyages' routes.
CREATE OR REPLACE VIEW fleet_voyage_sectors_view AS
SELECT CAST(v.fc_id AS text) AS fc_id, v.sub_register, v.sub_name, v.returned_at, v.sector, v.leg
FROM companion_voyage_loot v
WHERE v.returned_at >= now() - interval '14 days';

-- Every sector of every voyage SubmarineTracker recorded over the Income window (Loot per Voyage, as the app's Income
-- tab: ipc/details.js loadLoot), with what it brought back and the window's days.
CREATE OR REPLACE VIEW fleet_voyage_loot_view AS
WITH settings AS (
  SELECT coalesce((SELECT CAST(setting_value AS int) FROM fleet_economics_settings
                   WHERE setting_key = 'income_window_days' AND setting_value IN (3, 7, 14, 30)), 7) AS window_days
)
SELECT CAST(l.fc_id AS text) AS fc_id, l.sub_register, l.returned_at, l.sector, l.sub_name, l.primary_item, l.primary_count,
       l.additional_item, l.additional_count, l.leg, st.window_days
FROM companion_voyage_loot l
CROSS JOIN settings st
WHERE l.returned_at > now() - make_interval(days => st.window_days);

-- How close the Next Rank dates came (the Subs tab's forecast line, ipc/subs.js loadForecastAccuracy): the dates the app
-- saved over the last 30 days, each sub slot's recorded rank over the last 31 (a value recorded again unchanged left out,
-- plus the last before), and when each recorded voyage came back over the last 31 days.
CREATE OR REPLACE VIEW fleet_rankup_forecast_view AS
SELECT f.cid, f.slot, f.rank, f.made_at, f.predicted_at, f.sub_name, CAST(c.fc_id AS text) AS fc_id, c.name AS character_name
FROM companion_rankup_forecast f
LEFT JOIN companion_character_current c ON c.cid = f.cid
WHERE f.made_at >= now() - interval '30 days';

CREATE OR REPLACE VIEW fleet_rankup_history_view AS
WITH series AS (
  SELECT DISTINCT f.cid, 'sub_rank:' || f.slot AS metric
  FROM companion_rankup_forecast f
  WHERE f.made_at >= now() - interval '30 days'
)
SELECT se.cid, se.metric, w.recorded_at, w.value
FROM series se
CROSS JOIN LATERAL (
  SELECT recorded_at, value FROM (
    SELECT h.recorded_at, h.value, lead(h.value) OVER newest_first AS earlier, row_number() OVER newest_first AS n
    FROM companion_metric_history h
    WHERE h.subject_type = 'character' AND h.subject_id = se.cid AND h.metric = se.metric
      AND h.recorded_at > now() - interval '31 days'
    WINDOW newest_first AS (ORDER BY h.recorded_at DESC)) inside
  WHERE n = 1 OR earlier IS DISTINCT FROM value) w
UNION ALL
SELECT se.cid, se.metric, b.recorded_at, b.value
FROM series se
CROSS JOIN LATERAL (
  SELECT h.recorded_at, h.value FROM companion_metric_history h
  WHERE h.subject_type = 'character' AND h.subject_id = se.cid AND h.metric = se.metric
    AND h.recorded_at <= now() - interval '31 days'
  ORDER BY h.recorded_at DESC LIMIT 1) b;

CREATE OR REPLACE VIEW fleet_voyage_returns_view AS
SELECT DISTINCT CAST(v.fc_id AS text) AS fc_id, v.sub_name, v.returned_at
FROM companion_voyage_loot v
WHERE v.returned_at >= now() - interval '31 days' AND v.sub_name IS NOT NULL;

-- ---- Shopping List (plugin 2.0's Shopping List stage), as the Parts tab's (ipc/shopping.js). The plugin works the list
-- out itself (ShoppingList.cs) from the Parts views, these two and the game's own recipes. ----

-- What the fleet holds that counts for the Shopping List, as ipc/shopping.js loadHeld: every character's bags,
-- crystals and saddlebags, its retainers' bags and crystals, and the Free Company chests and crystals (one chest
-- however many tracked members the FC has). One row per item (high quality counted with normal), who holds it ("Name
-- (World)", "Character 123" when not known, or the Free Company's name) and where ("Bags", "Crystals", "Saddlebag",
-- "Retainer Amy", "FC chest").
CREATE OR REPLACE VIEW fleet_shopping_held_view AS
SELECT item_id, who, place, CAST(sum(quantity) AS bigint) AS qty
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

-- The Shopping List's ticks (how many of each part on it have been crafted), saved by the app as JSON
-- (subs/shopping-progress.js): one row, empty when nothing has been ticked.
CREATE OR REPLACE VIEW fleet_shopping_progress_view AS
SELECT setting_value AS progress FROM tier3_settings WHERE setting_key = 'shopping_progress';

-- The settings the app and the plugin share (Settings → General): the Warnings (low repair kits and ceruleum, 0: off;
-- the FC house warning, 1 to 44 days, 7 by default; not synced, 10 to 1440 minutes, 30 by default), the repair kit
-- cost (null: not set), Live Market Prices, and "What do you run?" (the saved choice, auto when none or unknown, and
-- what "auto" decides from: built subs and retainers with a class among the tracked characters, as ipc/play-style.js).
CREATE OR REPLACE VIEW fleet_settings_view AS
SELECT coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_repair_kit_threshold'), 0) AS repair_kit_threshold,
       coalesce((SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'low_ceruleum_threshold'), 0) AS ceruleum_threshold,
       coalesce((SELECT setting_value::int FROM fleet_economics_settings
                 WHERE setting_key = 'house_warn_days' AND setting_value = round(setting_value) AND setting_value BETWEEN 1 AND 44), 7) AS house_warn_days,
       coalesce((SELECT setting_value::int FROM fleet_economics_settings
                 WHERE setting_key = 'not_synced_after_minutes' AND setting_value = round(setting_value) AND setting_value BETWEEN 10 AND 1440), 30) AS not_synced_minutes,
       (SELECT setting_value FROM fleet_economics_settings WHERE setting_key = 'repair_kit_cost') AS repair_kit_cost,
       coalesce((SELECT setting_value = 'true' FROM tier3_settings WHERE setting_key = 'universalis_enabled'), false) AS live_prices,
       coalesce((SELECT setting_value FROM tier3_settings WHERE setting_key = 'play_style' AND setting_value IN ('auto', 'both', 'subs', 'retainers')), 'auto') AS play_style_choice,
       (SELECT count(*)::int FROM companion_submarine_snapshot s WHERE s.level > 0 AND s.cid IN (SELECT cid FROM companion_character_current)) AS subs_built,
       (SELECT count(*)::int FROM companion_retainer_lookup r WHERE r.job > 0 AND r.owner_cid IN (SELECT cid FROM companion_character_current)) AS retainers_with_class;
