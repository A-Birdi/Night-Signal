-- Night Signal control plane: initial schema (SQLite; local development and automated tests).
-- Keep in step with ../postgres/0001_initial.sql. Application SQL is shared by both dialects, so column
-- names/types must stay equivalent. Timestamps are written only by column defaults / CURRENT_TIMESTAMP.
-- Applied by the control plane's migration runner inside one transaction and recorded in schema_migrations.

CREATE TABLE accounts (
    account_id   TEXT PRIMARY KEY CHECK (length(account_id) BETWEEN 8 AND 64), -- identity provider JWT "sub"
    created_at   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE player_cards (
    account_id   TEXT PRIMARY KEY REFERENCES accounts(account_id),
    display_name TEXT NOT NULL,                    -- not unique by design (spec §11)
    revision     INTEGER NOT NULL CHECK (revision >= 1),
    updated_at   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE wallets (
    account_id   TEXT PRIMARY KEY REFERENCES accounts(account_id),
    balance      INTEGER NOT NULL CHECK (balance BETWEEN 0 AND 9999999),
    revision     INTEGER NOT NULL DEFAULT 0
);

-- Append-only money ledger. idempotency_key = "<event>/<account>/<reward-type>" (or purchase/starter keys).
CREATE TABLE ledger_entries (
    entry_id         INTEGER PRIMARY KEY AUTOINCREMENT,
    account_id       TEXT NOT NULL REFERENCES accounts(account_id),
    idempotency_key  TEXT NOT NULL UNIQUE,
    reward_type      TEXT NOT NULL,
    match_id         TEXT,
    item_ref         TEXT,
    requested_amount INTEGER NOT NULL,             -- what the rules computed (credits) or the price (debits, negative)
    applied_amount   INTEGER NOT NULL,             -- what actually changed the balance
    clamped_amount   INTEGER NOT NULL DEFAULT 0 CHECK (clamped_amount >= 0), -- lost to the 9,999,999 cap
    balance_after    INTEGER NOT NULL CHECK (balance_after BETWEEN 0 AND 9999999),
    created_at       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX ledger_entries_account ON ledger_entries(account_id, entry_id);

CREATE TRIGGER ledger_entries_no_update BEFORE UPDATE ON ledger_entries
BEGIN SELECT RAISE(ABORT, 'ledger_entries is append-only'); END;
CREATE TRIGGER ledger_entries_no_delete BEFORE DELETE ON ledger_entries
BEGIN SELECT RAISE(ABORT, 'ledger_entries is append-only'); END;

CREATE TABLE owned_cars (
    account_id   TEXT NOT NULL REFERENCES accounts(account_id),
    car_id       TEXT NOT NULL,
    source       TEXT NOT NULL CHECK (source IN ('starter', 'purchase')),
    acquired_at  TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, car_id)
);

CREATE TABLE stage_clears (
    account_id   TEXT NOT NULL REFERENCES accounts(account_id),
    mode         TEXT NOT NULL CHECK (mode IN ('normal', 'hard')),
    stage        INTEGER NOT NULL CHECK (stage BETWEEN 1 AND 30),
    match_id     TEXT NOT NULL,
    cleared_at   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT stage_clears_once UNIQUE (account_id, mode, stage)
);

CREATE TABLE challenge_unlocks (
    account_id   TEXT NOT NULL REFERENCES accounts(account_id),
    challenge_id TEXT NOT NULL,
    tier         TEXT NOT NULL CHECK (tier IN ('bronze', 'silver', 'gold')),
    match_id     TEXT NOT NULL,
    unlocked_at  TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT challenge_unlocks_once UNIQUE (account_id, challenge_id)
);

CREATE TABLE cosmetics_owned (
    account_id   TEXT NOT NULL REFERENCES accounts(account_id),
    cosmetic_id  TEXT NOT NULL,
    source       TEXT NOT NULL,
    acquired_at  TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, cosmetic_id)
);

-- Server-only: frozen match configuration and the per-match results HMAC secret.
CREATE TABLE matches (
    match_id       TEXT PRIMARY KEY,
    convoy_id      TEXT NOT NULL,
    server_id      TEXT NOT NULL,
    config_json    TEXT NOT NULL,
    results_secret TEXT NOT NULL,
    state          TEXT NOT NULL CHECK (state IN ('allocated', 'settled', 'aborted')),
    results_sha256 TEXT,
    created_at     TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    settled_at     TEXT
);

CREATE TABLE match_results (
    match_id     TEXT NOT NULL REFERENCES matches(match_id),
    account_id   TEXT NOT NULL REFERENCES accounts(account_id),
    receipt_json TEXT NOT NULL,
    created_at   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (match_id, account_id)
);
