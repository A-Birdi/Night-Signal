-- Night Signal control plane: the race diary's read marks (spec §5.3 race diary; Appendix E CH70). PostgreSQL / Supabase;
-- equivalent to ../sqlite/0009_diary_reads.sql. NOT EXECUTED in this environment (no PostgreSQL server available).
-- Row-level security: ../../postgres/rls_0009_diary.sql.

CREATE TABLE diary_reads (
    account_id text NOT NULL REFERENCES accounts(account_id),
    entry_id   text NOT NULL CHECK (length(entry_id) BETWEEN 1 AND 64),
    read_at    timestamptz NOT NULL DEFAULT CURRENT_TIMESTAMP,
    PRIMARY KEY (account_id, entry_id)
);
