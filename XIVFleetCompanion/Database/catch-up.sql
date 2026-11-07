-- Brings a database made before the app kept track of database updates up to the layout of db/schema.sql:
-- whatever database versions 1 and 6 to 9 add that is still missing. The app runs it once,
-- the first time it connects to such a database (db/apply-updates.js), then records version 9.
--
-- Each step runs only when what it adds is missing (and says so with a NOTICE), so a database that is
-- already up to date is only looked at, and the app's login needs no extra rights for it. Data is never
-- removed. Databases still on the old companion_character_snapshot table are refused by the app before
-- this runs (they're from before plugin 0.4.0).

DO $$
BEGIN
    -- database version 1: values over time, for Trends.
    IF to_regclass('companion_metric_history') IS NULL THEN
        CREATE TABLE companion_metric_history (
            recorded_at  timestamptz NOT NULL DEFAULT now(),
            subject_type text        NOT NULL,
            subject_id   numeric     NOT NULL,
            metric       text        NOT NULL,
            value        numeric     NOT NULL,
            label        text
        );
        RAISE NOTICE 'added companion_metric_history';
    ELSIF NOT EXISTS (SELECT 1 FROM information_schema.columns
                      WHERE table_name = 'companion_metric_history' AND column_name = 'label'
                        AND table_schema = current_schema()) THEN
        ALTER TABLE companion_metric_history ADD COLUMN label text;
        RAISE NOTICE 'added companion_metric_history.label';
    END IF;
    IF to_regclass('companion_metric_history_subject_idx') IS NULL THEN
        CREATE INDEX companion_metric_history_subject_idx
            ON companion_metric_history (subject_type, subject_id, metric, recorded_at DESC);
        RAISE NOTICE 'added companion_metric_history_subject_idx';
    END IF;
    IF to_regclass('companion_metric_history_metric_idx') IS NULL THEN
        CREATE INDEX companion_metric_history_metric_idx
            ON companion_metric_history (metric, recorded_at DESC);
        RAISE NOTICE 'added companion_metric_history_metric_idx';
    END IF;
    IF to_regclass('companion_metric_latest') IS NULL THEN
        CREATE TABLE companion_metric_latest (
            subject_type text        NOT NULL,
            subject_id   numeric     NOT NULL,
            metric       text        NOT NULL,
            value        numeric     NOT NULL,
            recorded_at  timestamptz NOT NULL,
            label        text,
            PRIMARY KEY (subject_type, subject_id, metric)
        );
        RAISE NOTICE 'added companion_metric_latest';
    ELSIF NOT EXISTS (SELECT 1 FROM information_schema.columns
                      WHERE table_name = 'companion_metric_latest' AND column_name = 'label'
                        AND table_schema = current_schema()) THEN
        ALTER TABLE companion_metric_latest ADD COLUMN label text;
        RAISE NOTICE 'added companion_metric_latest.label';
    END IF;

    -- database version 6: each sub's workshop slot, filled from the default names.
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'companion_submarine_snapshot' AND column_name = 'slot'
                     AND table_schema = current_schema()) THEN
        ALTER TABLE companion_submarine_snapshot ADD COLUMN slot int;
        UPDATE companion_submarine_snapshot
        SET slot = substring(sub_name FROM '^Submersible-([1-4])$')::int
        WHERE slot IS NULL AND sub_name ~ '^Submersible-[1-4]$';
        RAISE NOTICE 'added companion_submarine_snapshot.slot';
    END IF;

    -- database version 7: when each retainer's items were last seen.
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'companion_retainer_lookup' AND column_name = 'items_seen_at'
                     AND table_schema = current_schema()) THEN
        ALTER TABLE companion_retainer_lookup ADD COLUMN items_seen_at timestamptz;
        RAISE NOTICE 'added companion_retainer_lookup.items_seen_at';
    END IF;

    -- database version 8: each PC's status, voyage loot, venture rewards, market listings.
    IF to_regclass('companion_pc_status') IS NULL THEN
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
            PRIMARY KEY (pc_name, account_label)
        );
        RAISE NOTICE 'added companion_pc_status';
    END IF;
    IF to_regclass('companion_voyage_loot') IS NULL THEN
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
            leg              int,
            PRIMARY KEY (fc_id, sub_register, returned_at, sector)
        );
        RAISE NOTICE 'added companion_voyage_loot';
    END IF;
    IF to_regclass('companion_voyage_loot_returned_idx') IS NULL THEN
        CREATE INDEX companion_voyage_loot_returned_idx ON companion_voyage_loot (returned_at);
        RAISE NOTICE 'added companion_voyage_loot_returned_idx';
    END IF;
    IF to_regclass('companion_venture_result') IS NULL THEN
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
        RAISE NOTICE 'added companion_venture_result';
    END IF;
    IF to_regclass('companion_venture_result_received_idx') IS NULL THEN
        CREATE INDEX companion_venture_result_received_idx ON companion_venture_result (received_at);
        RAISE NOTICE 'added companion_venture_result_received_idx';
    END IF;
    IF to_regclass('companion_market_listing') IS NULL THEN
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
        RAISE NOTICE 'added companion_market_listing';
    END IF;

    -- database version 9: the order each voyage ran its sectors in.
    IF NOT EXISTS (SELECT 1 FROM information_schema.columns
                   WHERE table_name = 'companion_voyage_loot' AND column_name = 'leg'
                     AND table_schema = current_schema()) THEN
        ALTER TABLE companion_voyage_loot ADD COLUMN leg int;
        RAISE NOTICE 'added companion_voyage_loot.leg';
    END IF;
END $$;
