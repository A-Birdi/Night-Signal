-- Night Signal control plane: spec §11 Player Card appearance. SQLite; keep in step with ../postgres/0006_player_card.sql.
-- Additive only: two nullable columns; existing cards keep their name and revision and get the default look.

-- The driver's appearance as validated canonical JSON (Core PlayerLooks; visual only, never gameplay).
ALTER TABLE player_cards ADD COLUMN look_json TEXT CHECK (look_json IS NULL OR length(look_json) <= 2048);
-- Optional, self-chosen pronouns shown on the card (plain text, no markup).
ALTER TABLE player_cards ADD COLUMN pronouns TEXT CHECK (pronouns IS NULL OR length(pronouns) <= 24);
