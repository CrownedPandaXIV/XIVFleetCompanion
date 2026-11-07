-- Moving the database to another PC (Settings → Database → Move the database to this PC, app 0.35.0).
-- At most one row. state: 'pausing' while the copy is made (plugins wait; the app moving it keeps
-- updated_at fresh), 'moved' in the old database (apps and plugins switch to host:port), 'arrived' in the
-- new one (moved here from from_host at moved_at; the PCs tab says which PCs haven't switched yet).
CREATE TABLE IF NOT EXISTS companion_database_move (
    id         int         PRIMARY KEY DEFAULT 1 CHECK (id = 1),
    state      text        NOT NULL CHECK (state IN ('pausing', 'moved', 'arrived')),
    host       text        NOT NULL,
    port       int         NOT NULL,
    from_host  text,
    started_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    moved_at   timestamptz
);

-- The login each PC's plugin uses (plugin 0.14.0), so a move can check every PC will be let in.
ALTER TABLE companion_pc_status ADD COLUMN IF NOT EXISTS db_user text;
