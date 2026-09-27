-- Night Signal control plane: Addendum 01 (course access, public handles, friends, soundtrack collection, Team Trial
-- team bests). PostgreSQL / Supabase; equivalent to ../sqlite/0002_addendum01.sql.
-- NOT EXECUTED in the development environment this was written in (no PostgreSQL server available).
-- Apply after 0001_initial.sql, then apply ../../postgres/rls_0002_addendum01.sql. Additive only.

CREATE TABLE course_entitlements (
    account_id  text NOT NULL REFERENCES accounts(account_id),
    course_id   text NOT NULL,
    source      text NOT NULL CHECK (source IN ('purchase', 'campaign-clear')),
    match_id    text,
    ledger_key  text,
    acquired_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, course_id)
);

CREATE TABLE player_handles (
    account_id       text PRIMARY KEY REFERENCES accounts(account_id),
    handle_display   text NOT NULL CHECK (length(handle_display) BETWEEN 3 AND 20),
    handle_canonical text NOT NULL CHECK (handle_canonical = lower(handle_display)),
    revision         bigint NOT NULL DEFAULT 1 CHECK (revision >= 1),
    claimed_at       timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at       timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT player_handles_canonical_unique UNIQUE (handle_canonical)
);

CREATE TABLE friendships (
    account_low  text NOT NULL REFERENCES accounts(account_id),
    account_high text NOT NULL REFERENCES accounts(account_id),
    state        text NOT NULL CHECK (state IN ('pending', 'accepted')),
    requested_by text NOT NULL,
    created_at   timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at   timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_low, account_high),
    CHECK (account_low < account_high),
    CHECK (requested_by IN (account_low, account_high))
);
CREATE INDEX friendships_high ON friendships(account_high);

CREATE TABLE blocks (
    blocker_id text NOT NULL REFERENCES accounts(account_id),
    blocked_id text NOT NULL REFERENCES accounts(account_id),
    created_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (blocker_id, blocked_id),
    CHECK (blocker_id <> blocked_id)
);
CREATE INDEX blocks_blocked ON blocks(blocked_id);

CREATE TABLE ost_entitlements (
    account_id  text NOT NULL REFERENCES accounts(account_id),
    cue_id      text NOT NULL,
    source_kind text NOT NULL CHECK (source_kind IN ('stage-first-normal-clear', 'stage-first-hard-clear', 'lieutenant-first-defeat', 'trial-first-victory')),
    source_ref  text NOT NULL,
    match_id    text,
    acquired_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, cue_id)
);

CREATE TABLE team_trial_bests (
    account_id text NOT NULL REFERENCES accounts(account_id),
    trial_id   text NOT NULL,
    difficulty text NOT NULL,
    humans     integer NOT NULL CHECK (humans BETWEEN 1 AND 6),
    kind       text NOT NULL CHECK (kind IN ('mean', 'best', 'drift')),
    team_value bigint NOT NULL,
    match_id   text NOT NULL,
    updated_at timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, trial_id, difficulty, humans)
);
