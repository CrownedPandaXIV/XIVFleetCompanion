-- App 1.6.0 and plugin 1.0.2.
--
-- Where each character last logged in: the plugin setup (PC and account label, as on the PCs tab) that last had it
-- logged in is the only one that syncs it, so an old copy of a character left in another setup's AutoRetainer can't
-- overwrite it. Written by the plugin.
CREATE TABLE IF NOT EXISTS companion_character_login (
    cid           numeric     PRIMARY KEY,
    pc_name       text        NOT NULL,
    account_label text        NOT NULL,
    logged_in_at  timestamptz NOT NULL
);

-- How many characters each setup left to another setup at its last sync, because they last logged in there (the PCs
-- tab shows it).
ALTER TABLE companion_pc_status ADD COLUMN IF NOT EXISTS characters_elsewhere int;

-- The app's daily look for new game data (db/game-data-notice.js), shared by every PC the app runs on so only one
-- asks GitHub each day. One row. changes: { file: when it last changed } for the files changed since checked_since;
-- confirmed_at: a check in Settings found nothing to update as of then.
CREATE TABLE IF NOT EXISTS companion_game_data_check (
    id            int         PRIMARY KEY CHECK (id = 1),
    checked_at    timestamptz NOT NULL,
    changes       jsonb       NOT NULL DEFAULT '{}',
    checked_since timestamptz,
    confirmed_at  timestamptz
);
