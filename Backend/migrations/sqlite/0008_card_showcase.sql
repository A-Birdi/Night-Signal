-- Night Signal control plane: spec §11 "chosen showcase records" on the Player Card. SQLite; keep in step with
-- ../postgres/0008_card_showcase.sql. Additive only: one nullable column (no showcase until chosen).

-- Up to three record keys the player chose (a JSON array); each must name one of their own settled records when
-- saved, and the public card shows the record's CURRENT value.
ALTER TABLE player_cards ADD COLUMN showcase_json TEXT CHECK (showcase_json IS NULL OR length(showcase_json) <= 400);
