-- Night Signal control plane: initial schema (PostgreSQL / Supabase).
-- Equivalent to ../sqlite/0001_initial.sql; the control plane's SQL is shared by both dialects.
-- NOT EXECUTED in the development environment this was written in (no PostgreSQL server available).
-- Apply with the Supabase CLI (copy into supabase/migrations) or let the control plane apply it with
-- Storage:ApplyMigrations=true using the service-role connection. Then apply ../../postgres/rls.sql.

CREATE TABLE accounts (
    account_id   text PRIMARY KEY CHECK (length(account_id) BETWEEN 8 AND 64), -- Supabase auth.users.id as text
    created_at   timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE player_cards (
    account_id   text PRIMARY KEY REFERENCES accounts(account_id),
    display_name text NOT NULL,
    revision     bigint NOT NULL CHECK (revision >= 1),
    updated_at   timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);

CREATE TABLE wallets (
    account_id   text PRIMARY KEY REFERENCES accounts(account_id),
    balance      bigint NOT NULL CHECK (balance BETWEEN 0 AND 9999999),
    revision     bigint NOT NULL DEFAULT 0
);

CREATE TABLE ledger_entries (
    entry_id         bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    account_id       text NOT NULL REFERENCES accounts(account_id),
    idempotency_key  text NOT NULL UNIQUE,
    reward_type      text NOT NULL,
    match_id         text,
    item_ref         text,
    requested_amount bigint NOT NULL,
    applied_amount   bigint NOT NULL,
    clamped_amount   bigint NOT NULL DEFAULT 0 CHECK (clamped_amount >= 0),
    balance_after    bigint NOT NULL CHECK (balance_after BETWEEN 0 AND 9999999),
    created_at       timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX ledger_entries_account ON ledger_entries(account_id, entry_id);

CREATE FUNCTION ledger_entries_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'ledger_entries is append-only';
END;
$$;
CREATE TRIGGER ledger_entries_no_update BEFORE UPDATE OR DELETE ON ledger_entries
    FOR EACH ROW EXECUTE FUNCTION ledger_entries_append_only();

CREATE TABLE owned_cars (
    account_id   text NOT NULL REFERENCES accounts(account_id),
    car_id       text NOT NULL,
    source       text NOT NULL CHECK (source IN ('starter', 'purchase')),
    acquired_at  timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, car_id)
);

CREATE TABLE stage_clears (
    account_id   text NOT NULL REFERENCES accounts(account_id),
    mode         text NOT NULL CHECK (mode IN ('normal', 'hard')),
    stage        integer NOT NULL CHECK (stage BETWEEN 1 AND 30),
    match_id     text NOT NULL,
    cleared_at   timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT stage_clears_once UNIQUE (account_id, mode, stage)
);

CREATE TABLE challenge_unlocks (
    account_id   text NOT NULL REFERENCES accounts(account_id),
    challenge_id text NOT NULL,
    tier         text NOT NULL CHECK (tier IN ('bronze', 'silver', 'gold')),
    match_id     text NOT NULL,
    unlocked_at  timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT challenge_unlocks_once UNIQUE (account_id, challenge_id)
);

CREATE TABLE cosmetics_owned (
    account_id   text NOT NULL REFERENCES accounts(account_id),
    cosmetic_id  text NOT NULL,
    source       text NOT NULL,
    acquired_at  timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, cosmetic_id)
);

CREATE TABLE matches (
    match_id       text PRIMARY KEY,
    convoy_id      text NOT NULL,
    server_id      text NOT NULL,
    config_json    text NOT NULL,
    results_secret text NOT NULL,
    state          text NOT NULL CHECK (state IN ('allocated', 'settled', 'aborted')),
    results_sha256 text,
    created_at     timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    settled_at     timestamptz
);

CREATE TABLE match_results (
    match_id     text NOT NULL REFERENCES matches(match_id),
    account_id   text NOT NULL REFERENCES accounts(account_id),
    receipt_json text NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (match_id, account_id)
);
