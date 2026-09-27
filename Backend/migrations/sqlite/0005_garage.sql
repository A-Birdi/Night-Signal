-- Night Signal control plane: Addendum 02 §8–10 ONLINE Garage (per-instance parts, loadout workspaces, Buy-and-Apply
-- quotes). SQLite; keep in step with ../postgres/0005_garage.sql (application SQL is shared by both dialects).
-- Additive only: no existing row, balance or receipt is rewritten. Every write goes through the control plane.

-- One row per owned car INSTANCE (Addendum 02 §9.1: two instances of the same model are independent). Rows are created
-- by the control plane for every owned_cars row that has none yet (ordinal 1); the id is deterministic, so a concurrent
-- creation resolves through the primary key. owned_cars stays the purchase/starter record.
CREATE TABLE car_instances (
    instance_id TEXT PRIMARY KEY CHECK (length(instance_id) BETWEEN 8 AND 64),
    account_id  TEXT NOT NULL REFERENCES accounts(account_id),
    car_id      TEXT NOT NULL,
    ordinal     INTEGER NOT NULL CHECK (ordinal >= 1),
    source      TEXT NOT NULL CHECK (source IN ('starter', 'purchase')),
    created_at  TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT car_instances_ordinal UNIQUE (account_id, car_id, ordinal)
);
CREATE INDEX car_instances_account ON car_instances(account_id);

-- Server-owned part ownership PER INSTANCE: a part is bought for, and stays owned by, one car instance (swapped-out parts
-- remain owned). The primary key makes a second grant of the same part to the same instance impossible.
CREATE TABLE car_part_ownership (
    instance_id TEXT NOT NULL REFERENCES car_instances(instance_id),
    part_id     TEXT NOT NULL,
    account_id  TEXT NOT NULL REFERENCES accounts(account_id),
    source      TEXT NOT NULL CHECK (source IN ('purchase')),
    quote_id    TEXT,                                     -- the settled Buy-and-Apply quote that granted it
    price       INTEGER NOT NULL CHECK (price >= 0),
    acquired_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (instance_id, part_id)
);
CREATE INDEX car_part_ownership_account ON car_part_ownership(account_id);

-- The whole Core CarBuildWorkspace document of one instance (applied build, ≥ 8 named loadouts, ≥ 5 visual presets,
-- protected references, draft, workshop session). revision = the workspace's optimistic-concurrency revision: every
-- accepted change is written with "WHERE revision = <the revision it was computed from>". The applied_* columns are a
-- denormalised copy of the server-computed applied build for inspection; the JSON is authoritative.
CREATE TABLE car_workspaces (
    instance_id        TEXT PRIMARY KEY REFERENCES car_instances(instance_id),
    account_id         TEXT NOT NULL REFERENCES accounts(account_id),
    revision           INTEGER NOT NULL CHECK (revision >= 1),
    schema_version     INTEGER NOT NULL,
    workspace_json     TEXT NOT NULL,
    applied_revision   INTEGER NOT NULL CHECK (applied_revision >= 1),
    applied_build_hash TEXT NOT NULL,
    applied_pi         INTEGER NOT NULL,
    updated_at         TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX car_workspaces_account ON car_workspaces(account_id);

-- Issued Buy-and-Apply quotes (Addendum 02 §10.4). The client only ever refers to a quote by id; the server settles the
-- stored quote, never a client copy.
CREATE TABLE garage_quotes (
    quote_id      TEXT PRIMARY KEY,
    account_id    TEXT NOT NULL REFERENCES accounts(account_id),
    instance_id   TEXT NOT NULL REFERENCES car_instances(instance_id),
    quote_json    TEXT NOT NULL,
    total         INTEGER NOT NULL CHECK (total > 0),
    expires_at_ms INTEGER NOT NULL,
    created_at    TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX garage_quotes_account ON garage_quotes(account_id, expires_at_ms);

-- The quote ledger: at most one settlement per quote id, written in the SAME transaction as the wallet debit
-- (ledger_entries, key garage-quote/<account>/<quote>), the part grants and the workspace change. A retry finds this row
-- and returns the stored result; nothing is charged or granted twice.
CREATE TABLE garage_quote_settlements (
    quote_id         TEXT PRIMARY KEY REFERENCES garage_quotes(quote_id),
    account_id       TEXT NOT NULL REFERENCES accounts(account_id),
    instance_id      TEXT NOT NULL REFERENCES car_instances(instance_id),
    debit            INTEGER NOT NULL CHECK (debit > 0),
    grants_json      TEXT NOT NULL,
    applied_revision INTEGER NOT NULL,
    build_hash       TEXT NOT NULL,
    balance_after    INTEGER NOT NULL CHECK (balance_after BETWEEN 0 AND 9999999),
    ledger_key       TEXT NOT NULL UNIQUE,
    record_json      TEXT NOT NULL,
    settled_at       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX garage_quote_settlements_account ON garage_quote_settlements(account_id);

CREATE TRIGGER garage_quote_settlements_no_update BEFORE UPDATE ON garage_quote_settlements
BEGIN SELECT RAISE(ABORT, 'garage_quote_settlements is append-only'); END;
CREATE TRIGGER garage_quote_settlements_no_delete BEFORE DELETE ON garage_quote_settlements
BEGIN SELECT RAISE(ABORT, 'garage_quote_settlements is append-only'); END;
