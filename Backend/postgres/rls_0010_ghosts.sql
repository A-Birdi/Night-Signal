-- Night Signal: RLS for migrations/postgres/0010_ghosts.sql. Apply after rls_0009_diary.sql.
-- NOT EXECUTED in this environment (no Supabase/PostgreSQL).
-- Players may READ their own kept ghosts; pending match ghosts are server-only; every write goes through the control plane
-- (service role). Friends' and convoy members' ghosts are served by the control plane, which checks the relationship.
ALTER TABLE match_ghosts ENABLE ROW LEVEL SECURITY;
ALTER TABLE ghosts       ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON match_ghosts, ghosts FROM anon, authenticated;
GRANT SELECT ON ghosts TO authenticated;
CREATE POLICY own_ghosts_read ON ghosts FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
GRANT ALL ON match_ghosts, ghosts TO service_role;
