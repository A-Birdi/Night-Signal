-- Night Signal control plane: Addendum 02 dormant convoy rooms (D208). PostgreSQL / Supabase; equivalent to
-- ../sqlite/0003_addendum02.sql. NOT EXECUTED in this environment (no PostgreSQL server available).
-- Server-only table: apply ../../postgres/rls_0003_addendum02.sql afterwards (RLS on, no client grants).
CREATE TABLE dormant_rooms (
    session_id       text PRIMARY KEY,
    convoy_id        text NOT NULL UNIQUE,
    snapshot_json    text NOT NULL,
    dormant_since_ms bigint NOT NULL,
    expires_at_ms    bigint NOT NULL CHECK (expires_at_ms > dormant_since_ms),
    saved_at         timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX dormant_rooms_expiry ON dormant_rooms(expires_at_ms);
