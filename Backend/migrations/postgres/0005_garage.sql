-- Night Signal control plane: Addendum 02 §8–10 ONLINE Garage (per-instance parts, loadout workspaces, Buy-and-Apply
-- quotes). PostgreSQL / Supabase; equivalent to ../sqlite/0005_garage.sql. NOT EXECUTED in this environment (no
-- PostgreSQL server available). Apply ../../postgres/rls_0005_garage.sql afterwards.

CREATE TABLE car_instances (
    instance_id text PRIMARY KEY CHECK (length(instance_id) BETWEEN 8 AND 64),
    account_id  text NOT NULL REFERENCES accounts(account_id),
    car_id      text NOT NULL,
    ordinal     integer NOT NULL CHECK (ordinal >= 1),
    source      text NOT NULL CHECK (source IN ('starter', 'purchase')),
    created_at  timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT car_instances_ordinal UNIQUE (account_id, car_id, ordinal)
);
CREATE INDEX car_instances_account ON car_instances(account_id);

CREATE TABLE car_part_ownership (
    instance_id text NOT NULL REFERENCES car_instances(instance_id),
    part_id     text NOT NULL,
    account_id  text NOT NULL REFERENCES accounts(account_id),
    source      text NOT NULL CHECK (source IN ('purchase')),
    quote_id    text,
    price       bigint NOT NULL CHECK (price >= 0),
    acquired_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (instance_id, part_id)
);
CREATE INDEX car_part_ownership_account ON car_part_ownership(account_id);

CREATE TABLE car_workspaces (
    instance_id        text PRIMARY KEY REFERENCES car_instances(instance_id),
    account_id         text NOT NULL REFERENCES accounts(account_id),
    revision           bigint NOT NULL CHECK (revision >= 1),
    schema_version     integer NOT NULL,
    workspace_json     text NOT NULL,
    applied_revision   bigint NOT NULL CHECK (applied_revision >= 1),
    applied_build_hash text NOT NULL,
    applied_pi         integer NOT NULL,
    updated_at         timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX car_workspaces_account ON car_workspaces(account_id);

CREATE TABLE garage_quotes (
    quote_id      text PRIMARY KEY,
    account_id    text NOT NULL REFERENCES accounts(account_id),
    instance_id   text NOT NULL REFERENCES car_instances(instance_id),
    quote_json    text NOT NULL,
    total         bigint NOT NULL CHECK (total > 0),
    expires_at_ms bigint NOT NULL,
    created_at    timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX garage_quotes_account ON garage_quotes(account_id, expires_at_ms);

CREATE TABLE garage_quote_settlements (
    quote_id         text PRIMARY KEY REFERENCES garage_quotes(quote_id),
    account_id       text NOT NULL REFERENCES accounts(account_id),
    instance_id      text NOT NULL REFERENCES car_instances(instance_id),
    debit            bigint NOT NULL CHECK (debit > 0),
    grants_json      text NOT NULL,
    applied_revision bigint NOT NULL,
    build_hash       text NOT NULL,
    balance_after    bigint NOT NULL CHECK (balance_after BETWEEN 0 AND 9999999),
    ledger_key       text NOT NULL UNIQUE,
    record_json      text NOT NULL,
    settled_at       timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP
);
CREATE INDEX garage_quote_settlements_account ON garage_quote_settlements(account_id);

CREATE FUNCTION garage_quote_settlements_append_only() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'garage_quote_settlements is append-only';
END;
$$;
CREATE TRIGGER garage_quote_settlements_no_update BEFORE UPDATE OR DELETE ON garage_quote_settlements
    FOR EACH ROW EXECUTE FUNCTION garage_quote_settlements_append_only();
