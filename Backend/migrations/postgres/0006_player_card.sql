-- Night Signal control plane: spec §11 Player Card appearance. PostgreSQL / Supabase; equivalent to
-- ../sqlite/0006_player_card.sql. NOT EXECUTED in this environment (no PostgreSQL server available). The existing
-- player_cards row-level security (rls.sql: own_card_read) covers the new columns; no policy change.

ALTER TABLE player_cards ADD COLUMN look_json text CHECK (look_json IS NULL OR length(look_json) <= 2048);
ALTER TABLE player_cards ADD COLUMN pronouns text CHECK (pronouns IS NULL OR length(pronouns) <= 24);
