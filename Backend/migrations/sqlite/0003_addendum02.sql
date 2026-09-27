-- Night Signal control plane: Addendum 02 dormant convoy rooms (D208). SQLite; keep in step with
-- ../postgres/0003_addendum02.sql. Server-only state: a compact JSON snapshot (identity, leadership epoch, intent and the
-- valid rejoin grants — never seats, readiness or a running race) kept at most 24 h after the last active member was lost
-- to disconnection. Expiry is fixed at dormancy; reconnect attempts and heartbeats never extend it.
CREATE TABLE dormant_rooms (
    session_id     TEXT PRIMARY KEY,
    convoy_id      TEXT NOT NULL UNIQUE,
    snapshot_json  TEXT NOT NULL,
    dormant_since_ms INTEGER NOT NULL,
    expires_at_ms  INTEGER NOT NULL CHECK (expires_at_ms > dormant_since_ms),
    saved_at       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX dormant_rooms_expiry ON dormant_rooms(expires_at_ms);
