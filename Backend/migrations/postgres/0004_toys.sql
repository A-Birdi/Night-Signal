-- Night Signal control plane: Addendum 02 'While We Wait' toy snapshots (§1.5, §11, D208, D209). PostgreSQL / Supabase;
-- equivalent to ../sqlite/0004_toys.sql. NOT EXECUTED in this environment (no PostgreSQL server available).
-- Server-only table: apply ../../postgres/rls_0004_toys.sql afterwards (RLS on, no client grants).
CREATE TABLE toy_snapshots (
    session_id         text PRIMARY KEY,
    revision           bigint NOT NULL CHECK (revision >= 0),
    snapshot_json      text NOT NULL,
    dormant_expires_ms bigint NULL,
    saved_at           timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX toy_snapshots_expiry ON toy_snapshots(dormant_expires_ms);
