-- Next Rank dates as the app worked them out (app 1.3.0), saved once an hour, so the Subs tab can say how
-- close they came to when each sub actually ranked up. A sub is its character and workshop slot. Kept 30 days.
CREATE TABLE IF NOT EXISTS companion_rankup_forecast (
    cid          numeric     NOT NULL,
    slot         int         NOT NULL,
    rank         int         NOT NULL,
    made_at      timestamptz NOT NULL,
    predicted_at timestamptz NOT NULL,
    sub_name     text,
    PRIMARY KEY (cid, slot, rank, made_at)
);
CREATE INDEX IF NOT EXISTS companion_rankup_forecast_made_at ON companion_rankup_forecast (made_at);
