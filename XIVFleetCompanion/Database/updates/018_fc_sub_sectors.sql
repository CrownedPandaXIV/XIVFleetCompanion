-- App 1.12.0 / plugin 2.6.0: which submarine sectors each Free Company has unlocked and explored, read from the game
-- while a character is in the FC's workshop, for the Sub Calculator's Route Finder (only routes the FC can send subs on,
-- and what to explore next). One row per FC and sector, as last read.
--   sector_id: the game's SubmarineExploration row
--   read_at:   when the plugin last read it
--   cid:       the character it was read on
-- Safe to run twice.
CREATE TABLE IF NOT EXISTS companion_fc_sub_sector (
    fc_id     numeric     NOT NULL,
    sector_id int         NOT NULL,
    unlocked  boolean     NOT NULL,
    explored  boolean     NOT NULL,
    read_at   timestamptz NOT NULL,
    cid       numeric,
    PRIMARY KEY (fc_id, sector_id)
);
