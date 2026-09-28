-- Night Signal control plane: spec §11 Player Card style. SQLite; keep in step with ../postgres/0007_card_style.sql.
-- Additive only: one nullable column; existing cards keep everything and show the catalogue's default style.

-- Background, frame, motif, title, layout, region and preferred car as validated canonical JSON (Core CardStyle;
-- customization.json "card"; reward items only when owned). Visual only, never gameplay.
ALTER TABLE player_cards ADD COLUMN style_json TEXT CHECK (style_json IS NULL OR length(style_json) <= 512);
