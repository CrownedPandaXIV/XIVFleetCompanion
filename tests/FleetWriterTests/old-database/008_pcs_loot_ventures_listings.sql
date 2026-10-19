-- Four new tables for plugin 0.10.0 and app 0.20.0: each PC's status, the loot of every voyage (from
-- SubmarineTracker), the reward of every venture (from AutoRetainer's statistics) and the retainers'
-- market listings (from AllaganTools).
-- Run once, before or after updating the plugin (safe to run more than once; instant; changes no data).
-- Until it has been run, the plugin and the app work as before without these.

BEGIN;

-- One row per PC and account label, rewritten at every sync. The check columns hold the last
-- "Check what I can see" result from that PC.
CREATE TABLE IF NOT EXISTS companion_pc_status (
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
    PRIMARY KEY (pc_name, account_label)
);

-- One row per sector of a voyage: what SubmarineTracker recorded when the sub came back.
-- sub_register is SubmarineTracker's id for the sub (when it was registered).
CREATE TABLE IF NOT EXISTS companion_voyage_loot (
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
    PRIMARY KEY (fc_id, sub_register, returned_at, sector)
);
CREATE INDEX IF NOT EXISTS companion_voyage_loot_returned_idx ON companion_voyage_loot (returned_at);

-- One row per venture reward, from AutoRetainer's statistics files.
CREATE TABLE IF NOT EXISTS companion_venture_result (
    owner_cid     numeric     NOT NULL,
    retainer_name text        NOT NULL,
    received_at   timestamptz NOT NULL,
    item_id       int         NOT NULL,
    hq            boolean     NOT NULL,
    quantity      int         NOT NULL,
    venture_id    int,
    PRIMARY KEY (owner_cid, retainer_name, received_at, item_id, hq)
);
CREATE INDEX IF NOT EXISTS companion_venture_result_received_idx ON companion_venture_result (received_at);

-- What each retainer has up for sale, and since when (first_seen_at: when the plugin first saw it).
CREATE TABLE IF NOT EXISTS companion_market_listing (
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

COMMIT;
