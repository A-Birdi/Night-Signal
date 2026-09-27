-- Night Signal control plane: Addendum 02 'While We Wait' toy snapshots (§1.5, §11, D208, D209). SQLite; keep in step
-- with ../postgres/0004_toys.sql. Server-only, NON-PROGRESSION state: the compact Core DowntimeSnapshot JSON of one convoy
-- session's five toys (boards, project, attempts, laps, Canvas document), never Credits, RP, records or unlocks.
-- dormant_expires_ms is set while the convoy is Dormant (fixed 24 h after the last member was lost to disconnection;
-- heartbeats never extend it) and NULL while it is active. Expired rows are deleted; rows whose convoy is not recovered
-- after a restart are deleted too (toy data never fabricates a live convoy).
CREATE TABLE toy_snapshots (
    session_id         TEXT PRIMARY KEY,
    revision           INTEGER NOT NULL CHECK (revision >= 0),
    snapshot_json      TEXT NOT NULL,
    dormant_expires_ms INTEGER NULL,
    saved_at           TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX toy_snapshots_expiry ON toy_snapshots(dormant_expires_ms);
