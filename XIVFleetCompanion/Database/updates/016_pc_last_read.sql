-- App 1.8.0 / plugin 2.0: a PC whose plugin only reads (it syncs no characters: syncing turned off, or none ticked)
-- checks in too, so the PCs tab lists it and a move of the database checks it can follow. last_read_at: when that PC's
-- plugin last read the database (every few minutes while the game runs). Safe to run twice.
ALTER TABLE companion_pc_status ADD COLUMN IF NOT EXISTS last_read_at timestamptz;
