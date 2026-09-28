-- Night Signal control plane: ghosts (spec §8; Appendix E CH68). PostgreSQL / Supabase; equivalent to
-- ../sqlite/0010_ghosts.sql. NOT EXECUTED in this environment (no PostgreSQL server available).
-- Row-level security: ../../postgres/rls_0010_ghosts.sql.

CREATE TABLE match_ghosts (
    match_id    text NOT NULL,
    account_id  text NOT NULL REFERENCES accounts(account_id),
    ghost_json  text NOT NULL CHECK (length(ghost_json) <= 409600),
    received_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (match_id, account_id)
);

CREATE TABLE ghosts (
    account_id    text NOT NULL REFERENCES accounts(account_id),
    course_id     text NOT NULL,
    format        text NOT NULL,
    rules_key     text NOT NULL CHECK (length(rules_key) <= 400),
    result_micros bigint NOT NULL CHECK (result_micros > 0),
    match_id      text NOT NULL,
    ghost_json    text NOT NULL CHECK (length(ghost_json) <= 409600),
    updated_at    timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, course_id, format, rules_key)
);
