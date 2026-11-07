-- App 1.8.0: which version of db/views.sql the database has (its checksum), so the app replaces the views only when
-- that file changed, and never with an older app's (see db/apply-updates.js ensureViews). One row.
CREATE TABLE IF NOT EXISTS companion_views (
    id          int         PRIMARY KEY CHECK (id = 1),
    checksum    text        NOT NULL,
    app_version text,
    applied_at  timestamptz NOT NULL DEFAULT now()
);
