-- Night Signal control plane: Addendum 01 (course access, public handles, friends, soundtrack collection, Team Trial
-- team bests). SQLite; keep in step with ../postgres/0002_addendum01.sql (application SQL is shared by both dialects).
-- Additive only: no existing row, balance, clear or receipt is rewritten.

-- Course-access ledger for the ONLINE progression domain (Addendum 01 §5.1). Starter courses (T00, C01–C04) are implicit
-- in Core CourseAccess and never stored. A course is owned once per account: a purchase and a campaign unlock racing each
-- other resolve through this primary key (the loser observes "already owned" and is not charged).
CREATE TABLE course_entitlements (
    account_id  TEXT NOT NULL REFERENCES accounts(account_id),
    course_id   TEXT NOT NULL,
    source      TEXT NOT NULL CHECK (source IN ('purchase', 'campaign-clear')),
    match_id    TEXT,          -- campaign-clear: the settled match that granted it
    ledger_key  TEXT,          -- purchase: idempotency key of the ledger debit
    acquired_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, course_id)
);

-- Public @handles (Addendum 01 §9.2, D08). Case-insensitive uniqueness is the UNIQUE constraint on the canonical
-- lowercase value; the chosen display casing is kept. Handles are identity/lookup keys, never login credentials.
CREATE TABLE player_handles (
    account_id       TEXT PRIMARY KEY REFERENCES accounts(account_id),
    handle_display   TEXT NOT NULL CHECK (length(handle_display) BETWEEN 3 AND 20),
    handle_canonical TEXT NOT NULL CHECK (handle_canonical = lower(handle_display)),
    revision         INTEGER NOT NULL DEFAULT 1 CHECK (revision >= 1),
    claimed_at       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at       TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT player_handles_canonical_unique UNIQUE (handle_canonical)
);

-- Friend relationships keyed by stable account IDs: exactly one row per unordered pair (account_low < account_high),
-- so retries and crossed requests can never create duplicate edges.
CREATE TABLE friendships (
    account_low  TEXT NOT NULL REFERENCES accounts(account_id),
    account_high TEXT NOT NULL REFERENCES accounts(account_id),
    state        TEXT NOT NULL CHECK (state IN ('pending', 'accepted')),
    requested_by TEXT NOT NULL,
    created_at   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_at   TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_low, account_high),
    CHECK (account_low < account_high),
    CHECK (requested_by IN (account_low, account_high))
);
CREATE INDEX friendships_high ON friendships(account_high);

CREATE TABLE blocks (
    blocker_id TEXT NOT NULL REFERENCES accounts(account_id),
    blocked_id TEXT NOT NULL REFERENCES accounts(account_id),
    created_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (blocker_id, blocked_id),
    CHECK (blocker_id <> blocked_id)
);
CREATE INDEX blocks_blocked ON blocks(blocked_id);

-- Soundtrack collection (Addendum 01 §11.3): one row per (account, cue), granted only with an eligible settled result.
-- Baseline cues are implicit (manifest) and never stored.
CREATE TABLE ost_entitlements (
    account_id  TEXT NOT NULL REFERENCES accounts(account_id),
    cue_id      TEXT NOT NULL,
    source_kind TEXT NOT NULL CHECK (source_kind IN ('stage-first-normal-clear', 'stage-first-hard-clear', 'lieutenant-first-defeat', 'trial-first-victory')),
    source_ref  TEXT NOT NULL,
    match_id    TEXT,
    acquired_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, cue_id)
);

-- Team Trial TEAM bests (Addendum 01 §3): labelled separately from personal bests and keyed by trial, difficulty and
-- roster composition (human count). team_value is Core TeamScore.Value (MEAN: sum of six contributions in ms; BEST: ms;
-- DRIFT: summed raw score).
CREATE TABLE team_trial_bests (
    account_id TEXT NOT NULL REFERENCES accounts(account_id),
    trial_id   TEXT NOT NULL,
    difficulty TEXT NOT NULL,
    humans     INTEGER NOT NULL CHECK (humans BETWEEN 1 AND 6),
    kind       TEXT NOT NULL CHECK (kind IN ('mean', 'best', 'drift')),
    team_value INTEGER NOT NULL,
    match_id   TEXT NOT NULL,
    updated_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, trial_id, difficulty, humans)
);
