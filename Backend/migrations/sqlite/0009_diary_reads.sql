-- Night Signal control plane: the race diary's read marks (spec §5.3 race diary; Appendix E CH70 "The Other Side of the
-- Card"). SQLite; keep in step with ../postgres/0009_diary_reads.sql. Additive only: a new table.

-- One row per diary entry the player has read (entry ids like 'crew:tea-hour'); the server records a crew introduction
-- only once its crew's stage is cleared on Normal. Settlement reads it for CH70.
CREATE TABLE diary_reads (
    account_id TEXT NOT NULL REFERENCES accounts(account_id),
    entry_id   TEXT NOT NULL CHECK (length(entry_id) BETWEEN 1 AND 64),
    read_at    TEXT NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, entry_id)
);
