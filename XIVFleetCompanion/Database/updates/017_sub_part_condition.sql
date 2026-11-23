-- App 1.12.0 / plugin 2.6.0: each sub part's condition, read from the game while a character is in the Free Company's
-- workshop, for the Sub Calculator's repair costs (how much each voyage wears a part). A row is kept only when a part's
-- item or condition changed since its last one, so a voyage shows as a drop and a repair as a rise.
--   sub_register: the game's id for the sub (when it was registered), as companion_voyage_loot has it
--   part_slot:    0 hull, 1 stern, 2 bow, 3 bridge
--   condition:    as the game keeps it, 0 to 30000 (30000 is 100%)
--   return_at:    when the sub's voyage at the time returns (or returned), to match readings with voyages
-- Safe to run twice.
CREATE TABLE IF NOT EXISTS companion_sub_part_condition (
    fc_id        numeric     NOT NULL,
    sub_register bigint      NOT NULL,
    part_slot    int         NOT NULL,
    read_at      timestamptz NOT NULL,
    item_id      int         NOT NULL,
    condition    int         NOT NULL,
    sub_name     text,
    cid          numeric,
    return_at    timestamptz,
    PRIMARY KEY (fc_id, sub_register, part_slot, read_at)
);
CREATE INDEX IF NOT EXISTS companion_sub_part_condition_read_idx ON companion_sub_part_condition (read_at);
