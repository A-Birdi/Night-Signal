-- Night Signal: Supabase row-level security for the Addendum 01 tables (migrations/postgres/0002_addendum01.sql).
-- Apply after rls.sql. Same model: game clients get READ-ONLY access to their own rows; every write goes through the
-- control plane (service role). Public lookups (handle → Player Card) are served by the control plane, which never
-- exposes e-mail, tokens or wallets; clients cannot enumerate other players' rows directly.
-- NOT EXECUTED in this environment (no Supabase/PostgreSQL).

ALTER TABLE course_entitlements ENABLE ROW LEVEL SECURITY;
ALTER TABLE player_handles      ENABLE ROW LEVEL SECURITY;
ALTER TABLE friendships         ENABLE ROW LEVEL SECURITY;
ALTER TABLE blocks              ENABLE ROW LEVEL SECURITY;
ALTER TABLE ost_entitlements    ENABLE ROW LEVEL SECURITY;
ALTER TABLE team_trial_bests    ENABLE ROW LEVEL SECURITY;

REVOKE ALL ON course_entitlements, player_handles, friendships, blocks, ost_entitlements, team_trial_bests FROM anon, authenticated;
GRANT SELECT ON course_entitlements, player_handles, friendships, blocks, ost_entitlements, team_trial_bests TO authenticated;

CREATE POLICY own_courses_read     ON course_entitlements FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_handle_read      ON player_handles      FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
-- Either party of a relationship can see that edge; nobody else can.
CREATE POLICY own_friendships_read ON friendships         FOR SELECT TO authenticated
    USING (account_low = (SELECT auth.uid())::text OR account_high = (SELECT auth.uid())::text);
-- Only the blocker sees a block (the blocked account is not told who blocked them).
CREATE POLICY own_blocks_read      ON blocks              FOR SELECT TO authenticated USING (blocker_id = (SELECT auth.uid())::text);
CREATE POLICY own_music_read       ON ost_entitlements    FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_team_bests_read  ON team_trial_bests    FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);

GRANT ALL ON course_entitlements, player_handles, friendships, blocks, ost_entitlements, team_trial_bests TO service_role;
