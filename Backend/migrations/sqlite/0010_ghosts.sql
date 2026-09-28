-- Night Signal control plane: ghosts (spec §8 "server-generated/validated replay samples"; Appendix E CH68). SQLite; keep in
-- step with ../postgres/0010_ghosts.sql. Additive only: two new tables.

-- A ghost the game server recorded for a human in a match, held until that match settles (validated then against the
-- settled finish).
CREATE TABLE match_ghosts (
    match_id    TEXT NOT NULL,
    account_id  TEXT NOT NULL REFERENCES accounts(account_id),
    ghost_json  TEXT NOT NULL CHECK (length(ghost_json) <= 409600),
    received_at TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (match_id, account_id)
);

-- Each account's best valid ghost per course, format and ruleset (course revision, direction, surface, physics and
-- scoring versions): a server-settled, reset-free legal finish.
CREATE TABLE ghosts (
    account_id    TEXT NOT NULL REFERENCES accounts(account_id),
    course_id     TEXT NOT NULL,
    format        TEXT NOT NULL,
    rules_key     TEXT NOT NULL CHECK (length(rules_key) <= 400),
    result_micros INTEGER NOT NULL CHECK (result_micros > 0),
    match_id      TEXT NOT NULL,
    ghost_json    TEXT NOT NULL CHECK (length(ghost_json) <= 409600),
    updated_at    TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, course_id, format, rules_key)
);
