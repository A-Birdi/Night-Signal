-- Night Signal: Supabase row-level security (spec §3.2 "database row/security policies and service roles
-- must enforce this even when someone bypasses the UI").
--
-- Model:
--   * Game clients (roles anon / authenticated, i.e. the public anon key + a user JWT) get READ-ONLY access to
--     their OWN rows. They have no INSERT/UPDATE/DELETE privilege or policy anywhere.
--   * Every write goes through the control plane, which connects with the service role (bypasses RLS) and runs
--     server-owned transactions. The service-role key / DB password never ships in the client or the repo.
--   * Server-only tables (matches with per-match HMAC secrets, schema_migrations) are invisible to clients.
--
-- Apply after migrations/postgres/0001_initial.sql. NOT EXECUTED in this environment (no Supabase/PostgreSQL).
-- auth.uid() is Supabase's helper returning the JWT "sub" as uuid; account_id stores it as text.

-- 1. Start from zero privileges for client roles.
REVOKE ALL ON ALL TABLES IN SCHEMA public FROM anon, authenticated;
REVOKE ALL ON ALL SEQUENCES IN SCHEMA public FROM anon, authenticated;
REVOKE ALL ON ALL FUNCTIONS IN SCHEMA public FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON TABLES FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON SEQUENCES FROM anon, authenticated;
ALTER DEFAULT PRIVILEGES IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM anon, authenticated;

-- 2. RLS on every table. Client roles are never table owners, so ENABLE is enough for them; the control plane
--    connects as the owner or as service_role (BYPASSRLS) and is therefore not filtered.
ALTER TABLE accounts          ENABLE ROW LEVEL SECURITY;
ALTER TABLE player_cards      ENABLE ROW LEVEL SECURITY;
ALTER TABLE wallets           ENABLE ROW LEVEL SECURITY;
ALTER TABLE ledger_entries    ENABLE ROW LEVEL SECURITY;
ALTER TABLE owned_cars        ENABLE ROW LEVEL SECURITY;
ALTER TABLE stage_clears      ENABLE ROW LEVEL SECURITY;
ALTER TABLE challenge_unlocks ENABLE ROW LEVEL SECURITY;
ALTER TABLE cosmetics_owned   ENABLE ROW LEVEL SECURITY;
ALTER TABLE matches           ENABLE ROW LEVEL SECURITY;
ALTER TABLE match_results     ENABLE ROW LEVEL SECURITY;
ALTER TABLE IF EXISTS schema_migrations ENABLE ROW LEVEL SECURITY; -- created by the control plane runner, if used

-- 3. Read-only, own-row access for signed-in players. No policies exist for INSERT/UPDATE/DELETE, and no
--    write privileges are granted, so client writes fail twice over. matches and schema_migrations get no
--    client grant or policy at all.
GRANT SELECT ON accounts, player_cards, wallets, ledger_entries, owned_cars, stage_clears,
                challenge_unlocks, cosmetics_owned, match_results TO authenticated;

CREATE POLICY own_account_read     ON accounts          FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_card_read        ON player_cards      FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_wallet_read      ON wallets           FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_ledger_read      ON ledger_entries    FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_cars_read        ON owned_cars        FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_clears_read      ON stage_clears      FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_challenges_read  ON challenge_unlocks FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_cosmetics_read   ON cosmetics_owned   FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_receipts_read    ON match_results     FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);

-- Public Player Card inspection (display name, rank) is served by the control plane, which omits private
-- fields; clients cannot read other players' rows directly.

-- 4. The control plane's service role keeps full access (it bypasses RLS by design in Supabase).
GRANT ALL ON ALL TABLES IN SCHEMA public TO service_role;
GRANT ALL ON ALL SEQUENCES IN SCHEMA public TO service_role;
