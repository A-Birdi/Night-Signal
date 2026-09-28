-- Night Signal control plane: spec §11 Player Card style. PostgreSQL / Supabase; equivalent to
-- ../sqlite/0007_card_style.sql. NOT EXECUTED in this environment (no PostgreSQL server available). The existing
-- player_cards row-level security (rls.sql: own_card_read) covers the new column; no policy change.

ALTER TABLE player_cards ADD COLUMN style_json text CHECK (style_json IS NULL OR length(style_json) <= 512);
