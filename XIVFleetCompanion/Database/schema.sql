-- XIV Fleet Companion: every table the plugin and the app use, as they are in the live database
-- (October 2026: plugin 0.10.0, app 0.20.0, after database versions 1 to 8, with the old n8n
-- tables and the old per-sync snapshot history removed).
--
-- For a NEW, EMPTY database only: it creates everything (and fills the two fixed name lists), and stops at the first table that already
-- exists (nothing is half-made, since it all runs in one transaction). The app runs it by itself when
-- it first connects to an empty database, and keeps every database up to date from then on with
-- db/catch-up.sql and the numbered files in db/updates/ (db/apply-updates.js). Each of those is also made
-- here, so this file is always where they lead. By hand:
--
--   psql -v ON_ERROR_STOP=1 -d <new database> -f db/schema.sql
--
-- Then start the app and use Settings > Game Data > Update to fill the ffxiv_ game tables.
--
-- Some timestamp columns are "without time zone" and two values have unusual types (fc_rank is text,
-- fleet_economics_settings.setting_value is numeric): that is how the live tables were made, and the
-- plugin and app are written for it, so it is kept here as is.

BEGIN;

-- ---------------------------------------------------------------------------------------------------
-- Written by the plugin on every sync
-- ---------------------------------------------------------------------------------------------------

-- One row per character: what the app shows, kept current by the plugin (database version 4).
CREATE TABLE companion_character_current (
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
CREATE INDEX companion_character_current_fc_id_idx ON companion_character_current (fc_id);

-- One row each time a character's name, world, account label or Free Company changes. Filled by the
-- trigger below, never written directly.
CREATE TABLE companion_character_changes (
    changed_at timestamptz NOT NULL DEFAULT now(),
    cid        numeric     NOT NULL,
    field      text        NOT NULL,   -- name, world, account_label or fc_id
    old_value  text,
    new_value  text
);
CREATE INDEX companion_character_changes_cid_idx ON companion_character_changes (cid, changed_at DESC);

CREATE FUNCTION companion_character_record_changes() RETURNS trigger
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

CREATE TRIGGER companion_character_current_changes
    AFTER UPDATE ON companion_character_current
    FOR EACH ROW
    WHEN (OLD.name IS DISTINCT FROM NEW.name
       OR OLD.world IS DISTINCT FROM NEW.world
       OR OLD.account_label IS DISTINCT FROM NEW.account_label
       OR OLD.fc_id IS DISTINCT FROM NEW.fc_id)
    EXECUTE FUNCTION companion_character_record_changes();

-- Values over time, for Trends: a row only when a value changes, plus an hourly reminder (database version 1).
-- subject_type is 'character' or 'fc'; subject_id is the character's cid or the FC's id.
CREATE TABLE companion_metric_history (
    recorded_at  timestamptz NOT NULL DEFAULT now(),
    subject_type text        NOT NULL,
    subject_id   numeric     NOT NULL,
    metric       text        NOT NULL,
    value        numeric     NOT NULL,
    label        text
);
CREATE INDEX companion_metric_history_subject_idx
    ON companion_metric_history (subject_type, subject_id, metric, recorded_at DESC);
CREATE INDEX companion_metric_history_metric_idx
    ON companion_metric_history (metric, recorded_at DESC);

-- The newest value of each metric, so the plugin can tell whether a value changed.
CREATE TABLE companion_metric_latest (
    subject_type text        NOT NULL,
    subject_id   numeric     NOT NULL,
    metric       text        NOT NULL,
    value        numeric     NOT NULL,
    recorded_at  timestamptz NOT NULL,
    label        text,
    PRIMARY KEY (subject_type, subject_id, metric)
);

-- Free Company and housing details, one row per character.
CREATE TABLE companion_character_housing (
    cid                     numeric     PRIMARY KEY,
    fc_id                   numeric,
    fc_name                 text,
    fc_points               int,
    fc_rank                 text,
    total_members           int,
    has_house               boolean,
    house_city              int,
    house_ward              int,
    house_plot              int,
    house_last_visited      timestamptz,
    updated_at              timestamptz NOT NULL DEFAULT now(),
    fc_master               text,
    fc_home_world_id        int,
    fc_founding_date        timestamptz,
    fc_eligibility_override timestamptz   -- FCTracker's eligibility date override
);

-- Each character's and retainer's items (retainer_id is the character's own cid for its own bags).
CREATE TABLE companion_inventory_snapshot (
    owner_cid         numeric     NOT NULL,
    retainer_id       numeric     NOT NULL,
    sorted_container  int         NOT NULL,
    sorted_slot_index int         NOT NULL,
    item_id           int,
    quantity          int,
    updated_at        timestamptz NOT NULL DEFAULT now(),
    gear_set_ids      int[],
    PRIMARY KEY (owner_cid, retainer_id, sorted_container, sorted_slot_index)
);

-- Each Free Company's chest.
CREATE TABLE companion_fc_inventory_snapshot (
    fc_id             numeric   NOT NULL,
    sorted_container  int       NOT NULL,
    sorted_slot_index int       NOT NULL,
    item_id           int       NOT NULL,
    quantity          int       NOT NULL,
    updated_at        timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (fc_id, sorted_container, sorted_slot_index)
);

CREATE TABLE companion_retainer_lookup (
    retainer_id       numeric   PRIMARY KEY,
    owner_cid         numeric   NOT NULL,
    name              text      NOT NULL,
    updated_at        timestamp NOT NULL DEFAULT now(),
    job               int,
    gil               bigint,
    has_venture       boolean,
    venture_id        int,
    venture_begins_at bigint,   -- Unix seconds
    venture_ends_at   bigint,   -- Unix seconds
    level             int,
    hire_order_index  int,
    items_seen_at     timestamptz   -- database version 7: when AllaganTools last saw its items
);

CREATE TABLE companion_submarine_snapshot (
    cid            numeric   NOT NULL,
    sub_name       text      NOT NULL,
    level          int,
    part1          int,
    part2          int,
    part3          int,
    part4          int,
    points         bytea,    -- the route's stops
    return_time    bigint,   -- Unix seconds
    updated_at     timestamp NOT NULL DEFAULT now(),
    current_exp    bigint,
    next_level_exp bigint,
    slot           int,      -- workshop slot 1-4 (plugin 0.5.0, database version 6), so a renamed sub keeps its place
    PRIMARY KEY (cid, sub_name)
);

-- From plugin 0.10.0 (database version 8): each PC's status, voyage loot, venture rewards, market listings.

-- One row per PC and account label, rewritten at every sync. The check columns hold the last
-- "Check what I can see" result from that PC.
CREATE TABLE companion_pc_status (
    pc_name                text        NOT NULL,
    account_label          text        NOT NULL DEFAULT '',
    plugin_version         text,
    last_sync_at           timestamptz,
    autoretainer_ready     boolean,
    allagantools_ready     boolean,
    fctracker_found        boolean,
    submarinetracker_found boolean,
    venture_stats_found    boolean,
    characters_synced      int,
    characters_left_out    int,
    problems               text[],
    check_text             text,
    check_to_fix           int,
    check_at               timestamptz,
    db_user                text,       -- the login the plugin uses (plugin 0.14.0, database update 10)
    characters_elsewhere   int,        -- left to another setup at the last sync (plugin 1.0.2, database update 12)
    last_read_at           timestamptz, -- when its plugin last read the database, synced or not (plugin 2.0, database update 16)
    PRIMARY KEY (pc_name, account_label)
);

-- One row per sector of a voyage: what SubmarineTracker recorded when the sub came back.
-- sub_register is SubmarineTracker's id for the sub (when it was registered).
CREATE TABLE companion_voyage_loot (
    fc_id            numeric     NOT NULL,
    sub_register     bigint      NOT NULL,
    returned_at      timestamptz NOT NULL,
    sector           int         NOT NULL,
    sub_name         text,
    primary_item     int,
    primary_count    int,
    primary_hq       boolean,
    additional_item  int,
    additional_count int,
    additional_hq    boolean,
    valid            boolean,
    leg              int,        -- the order the voyage ran its sectors in: 1 for the first visited
    PRIMARY KEY (fc_id, sub_register, returned_at, sector)
);
CREATE INDEX companion_voyage_loot_returned_idx ON companion_voyage_loot (returned_at);

-- One row per venture reward, from AutoRetainer's statistics files.
CREATE TABLE companion_venture_result (
    owner_cid     numeric     NOT NULL,
    retainer_name text        NOT NULL,
    received_at   timestamptz NOT NULL,
    item_id       int         NOT NULL,
    hq            boolean     NOT NULL,
    quantity      int         NOT NULL,
    venture_id    int,
    PRIMARY KEY (owner_cid, retainer_name, received_at, item_id, hq)
);
CREATE INDEX companion_venture_result_received_idx ON companion_venture_result (received_at);

-- What each retainer has up for sale, and since when (first_seen_at: when the plugin first saw it).
CREATE TABLE companion_market_listing (
    retainer_id   numeric     NOT NULL,
    owner_cid     numeric     NOT NULL,
    slot          int         NOT NULL,
    item_id       int         NOT NULL,
    quantity      int         NOT NULL,
    hq            boolean     NOT NULL,
    unit_price    bigint      NOT NULL,
    first_seen_at timestamptz NOT NULL DEFAULT now(),
    updated_at    timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (retainer_id, slot)
);

-- ---------------------------------------------------------------------------------------------------
-- Moving the database to another PC (database update 10)
-- ---------------------------------------------------------------------------------------------------

-- At most one row. state: 'pausing' while the copy is made (plugins wait; the app moving it keeps
-- updated_at fresh), 'moved' in the old database (apps and plugins switch to host:port), 'arrived' in the
-- new one (moved here from from_host at moved_at; the PCs tab says which PCs haven't switched yet).
CREATE TABLE companion_database_move (
    id         int         PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    state      text        NOT NULL CHECK (state IN ('pausing', 'moved', 'arrived')),
    host       text        NOT NULL,
    port       int         NOT NULL,
    from_host  text,
    started_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    moved_at   timestamptz
);

-- ---------------------------------------------------------------------------------------------------
-- Next Rank dates, to check them against real rank-ups (database update 11)
-- ---------------------------------------------------------------------------------------------------

-- Next Rank dates as the app worked them out (app 1.3.0), saved once an hour, so the Subs tab can say how
-- close they came to when each sub actually ranked up. A sub is its character and workshop slot. Kept 30 days.
CREATE TABLE companion_rankup_forecast (
    cid          numeric     NOT NULL,
    slot         int         NOT NULL,
    rank         int         NOT NULL,
    made_at      timestamptz NOT NULL,
    predicted_at timestamptz NOT NULL,
    sub_name     text,
    PRIMARY KEY (cid, slot, rank, made_at)
);
CREATE INDEX companion_rankup_forecast_made_at ON companion_rankup_forecast (made_at);

-- Where each character last logged in (plugin 1.0.2, database update 12): the plugin setup (PC and account label)
-- that last had it logged in is the only one that syncs it, so an old copy in another setup's AutoRetainer can't
-- overwrite it.
CREATE TABLE companion_character_login (
    cid           numeric     PRIMARY KEY,
    pc_name       text        NOT NULL,
    account_label text        NOT NULL,
    logged_in_at  timestamptz NOT NULL
);

-- ---------------------------------------------------------------------------------------------------
-- Written by the app (your settings and choices)
-- ---------------------------------------------------------------------------------------------------

CREATE TABLE character_sort_order (
    cid        numeric     PRIMARY KEY,
    sort_index int         NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE tier3_settings (
    setting_key   text      PRIMARY KEY,
    setting_value text,
    updated_at    timestamp NOT NULL DEFAULT now()
);

CREATE TABLE fleet_economics_settings (
    setting_key   text      PRIMARY KEY,
    setting_value numeric,
    updated_at    timestamp NOT NULL DEFAULT now()
);

CREATE TABLE sub_craft_toggle (
    cid           numeric   NOT NULL,
    sub_name      text      NOT NULL,
    craft_enabled boolean   NOT NULL DEFAULT false,
    updated_at    timestamp NOT NULL DEFAULT now(),
    PRIMARY KEY (cid, sub_name)
);

CREATE TABLE sub_build_rules (
    id           serial    PRIMARY KEY,
    rank         int       NOT NULL,
    cid_override numeric,
    sub_filter   text,
    hull         text,
    stern        text,
    bow          text,
    bridge       text,
    updated_at   timestamp NOT NULL DEFAULT now()
);

CREATE TABLE retainer_gear_targets (
    retainer_id  numeric     PRIMARY KEY,
    target_mode  text        CHECK (target_mode = ANY (ARRAY['level', 'ilvl'])),
    target_value int,
    only_crafted boolean     NOT NULL DEFAULT false,
    updated_at   timestamptz NOT NULL DEFAULT now()
);

-- Imported from the Fight Club route spreadsheet.
CREATE TABLE fight_club_routes (
    id              serial    PRIMARY KEY,
    oj_24h_pct      numeric,
    gil_sub_day     numeric,
    route           text      NOT NULL,
    hours           numeric,
    rec_build_notes text,
    unmod_min_rank  text,
    min_rank_build  text,
    gil_sub         numeric,
    gil_fc          numeric,
    gil_hr          numeric,
    gil_day         numeric,
    gil_week        numeric,
    gil_month       numeric,
    gil_year        numeric,
    avg_exp         numeric,
    avg_exp_hr      numeric,
    repair_times    text,
    notes           text,
    fc_points       numeric,
    gc_ilvl_day     numeric,
    qvs_day         numeric,
    fuel_day        numeric,
    imported_at     timestamp NOT NULL DEFAULT now(),
    patch_label     text
);

-- One row per Settings > Game Data update.
CREATE TABLE companion_game_data_log (
    id         serial      PRIMARY KEY,
    applied_at timestamptz NOT NULL DEFAULT now(),
    source     jsonb,
    summary    jsonb
);

-- The app's daily look for new game data (app 1.6.0, database update 12), shared by every PC so only one asks
-- GitHub each day. One row.
CREATE TABLE companion_game_data_check (
    id            int         PRIMARY KEY CHECK (id = 1),
    checked_at    timestamptz NOT NULL,
    changes       jsonb       NOT NULL DEFAULT '{}',
    checked_since timestamptz,
    confirmed_at  timestamptz
);

-- Which database updates have been applied (db/apply-updates.js): version 9 is this layout as of the
-- last of the old plugin scripts, each later version one file of db/updates/. Filled by the app.
CREATE TABLE companion_schema_version (
    version     int         PRIMARY KEY,
    name        text        NOT NULL,
    applied_at  timestamptz NOT NULL DEFAULT now(),
    app_version text
);

-- ---------------------------------------------------------------------------------------------------
-- Fixed look-up lists
-- ---------------------------------------------------------------------------------------------------

CREATE TABLE companion_inventory_type_lookup (
    sorted_container int  PRIMARY KEY,
    display_name     text NOT NULL
);

CREATE TABLE housing_key_city_lookup (
    city_id   int  PRIMARY KEY,
    city_name text NOT NULL
);

-- The names as entered in the live database. sorted_container numbers are the game's inventory types.
INSERT INTO companion_inventory_type_lookup (sorted_container, display_name) VALUES
    (0, 'Inventory 1'),
    (1, 'Inventory 2'),
    (2, 'Inventory 3'),
    (3, 'Inventory 4'),
    (1000, 'GearSet0'),
    (1001, 'GearSet1'),
    (2000, 'Currency'),
    (2001, 'Crystal'),
    (2003, 'Mail'),
    (2004, 'KeyItem'),
    (2005, 'HandIn'),
    (2007, 'DamagedGear'),
    (2008, 'UNKNOWN_2008'),
    (2009, 'Examine'),
    (2500, 'Armoire'),
    (2501, 'GlamourChest'),
    (2502, 'FreeCompanyCurrency'),
    (3200, 'ArmoryOff'),
    (3201, 'ArmoryHead'),
    (3202, 'ArmoryBody'),
    (3203, 'ArmoryHand'),
    (3204, 'ArmoryWaist'),
    (3205, 'ArmoryLegs'),
    (3206, 'ArmoryFeet'),
    (3207, 'ArmoryEar'),
    (3208, 'ArmoryNeck'),
    (3209, 'ArmoryWrist'),
    (3300, 'ArmoryRing'),
    (3400, 'ArmorySoulCrystal'),
    (3500, 'ArmoryMain'),
    (4000, 'Saddlebag 1'),
    (4001, 'Saddlebag 2'),
    (4100, 'Premium Saddlebag 1'),
    (4101, 'Premium Saddlebag 2'),
    (10000, 'Retainer Bag 1'),
    (10001, 'Retainer Bag 2'),
    (10002, 'Retainer Bag 3'),
    (10003, 'Retainer Bag 4'),
    (10004, 'Retainer Bag 5'),
    (10005, 'Retainer Bag (unused 6)'),
    (10006, 'Retainer Bag (unused 7)'),
    (11000, 'RetainerEquippedGear'),
    (12000, 'RetainerGil'),
    (12001, 'RetainerCrystal'),
    (12002, 'RetainerMarket'),
    (20000, 'FC Chest 1'),
    (20001, 'FC Chest 2'),
    (20002, 'FC Chest 3'),
    (20003, 'FC Chest 4'),
    (20004, 'FC Chest 5'),
    (20005, 'FC Chest 6'),
    (20006, 'FC Chest 7'),
    (20007, 'FC Chest 8'),
    (20008, 'FC Chest 9'),
    (20009, 'FC Chest 10'),
    (20010, 'FC Chest 11'),
    (22000, 'FreeCompanyGil'),
    (22001, 'FreeCompanyCrystal'),
    (25000, 'HousingExteriorAppearance'),
    (25001, 'HousingExteriorPlacedItems'),
    (25002, 'HousingInteriorAppearance'),
    (25003, 'HousingInteriorPlacedItems1'),
    (25004, 'HousingInteriorPlacedItems2'),
    (25005, 'HousingInteriorPlacedItems3'),
    (25006, 'HousingInteriorPlacedItems4'),
    (25007, 'HousingInteriorPlacedItems5'),
    (25008, 'HousingInteriorPlacedItems6'),
    (25009, 'HousingInteriorPlacedItems7'),
    (25010, 'HousingInteriorPlacedItems8'),
    (25011, 'HousingInteriorPlacedItems9'),
    (25012, 'HousingInteriorPlacedItems10'),
    (25013, 'HousingInteriorPlacedItems11'),
    (25014, 'HousingInteriorPlacedItems12'),
    (25200, 'HousingExteriorPlacedItems2'),
    (27000, 'HousingExteriorStoreroom'),
    (27001, 'HousingInteriorStoreroom1'),
    (27002, 'HousingInteriorStoreroom2'),
    (27003, 'HousingInteriorStoreroom3'),
    (27004, 'HousingInteriorStoreroom4'),
    (27005, 'HousingInteriorStoreroom5'),
    (27006, 'HousingInteriorStoreroom6'),
    (27007, 'HousingInteriorStoreroom7'),
    (27008, 'HousingInteriorStoreroom8'),
    (27009, 'HousingInteriorStoreroom9'),
    (27010, 'HousingInteriorStoreroom10'),
    (27011, 'HousingInteriorStoreroom11'),
    (27200, 'HousingExteriorStoreroom2');

INSERT INTO housing_key_city_lookup (city_id, city_name) VALUES
    (2, 'Lavender Beds'),
    (8, 'Mist'),
    (9, 'Goblet'),
    (70, 'Empyreum'),
    (111, 'Shirogane');

-- ---------------------------------------------------------------------------------------------------
-- Game data (from xivapi/ffxiv-datamining). Each starts with only its id column: Settings > Game Data
-- > Update adds every other column of the game's file (all text, named like item_result) and fills
-- them, then indexes the id columns.
-- ---------------------------------------------------------------------------------------------------

CREATE TABLE ffxiv_baseparam            (id text);
CREATE TABLE ffxiv_classjob             (id text);
CREATE TABLE ffxiv_classjobcategory     (id text);
CREATE TABLE ffxiv_companycraftpart     (id text);
CREATE TABLE ffxiv_companycraftprocess  (id text);
CREATE TABLE ffxiv_companycraftsequence (id text);
CREATE TABLE ffxiv_companycraftsupplyitem (id text);
CREATE TABLE ffxiv_equipslotcategory    (id text);
CREATE TABLE ffxiv_gilshopitem          (id text);
CREATE TABLE ffxiv_housinglandset       (id text);
CREATE TABLE ffxiv_item                 (id text);
CREATE TABLE ffxiv_placename            (id text);
CREATE TABLE ffxiv_recipe               (id text);
CREATE TABLE ffxiv_retainertask         (id text);
CREATE TABLE ffxiv_retainertasknormal   (id text);
CREATE TABLE ffxiv_retainertaskrandom   (id text);
CREATE TABLE ffxiv_submarineexploration (id text);
CREATE TABLE ffxiv_submarinemap         (id text);
CREATE TABLE ffxiv_submarinepart        (id text);
CREATE TABLE ffxiv_submarinerank        (id text);
CREATE TABLE ffxiv_territorytype        (id text);
CREATE TABLE ffxiv_world                (id text);


-- Which version of db/views.sql the database has (database update 15); the views themselves come from that file.
CREATE TABLE companion_views (
    id          int         PRIMARY KEY CHECK (id = 1),
    checksum    text        NOT NULL,
    app_version text,
    applied_at  timestamptz NOT NULL DEFAULT now()
);

COMMIT;
