-- Night Signal control plane: spec §11 "chosen showcase records" on the Player Card. PostgreSQL / Supabase; equivalent to
-- ../sqlite/0008_card_showcase.sql. NOT EXECUTED in this environment (no PostgreSQL server available). The existing
-- player_cards row-level security (rls.sql: own_card_read) covers the new column; no policy change.

ALTER TABLE player_cards ADD COLUMN showcase_json text CHECK (showcase_json IS NULL OR length(showcase_json) <= 400);
