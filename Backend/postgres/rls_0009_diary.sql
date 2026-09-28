-- Night Signal: RLS for migrations/postgres/0009_diary_reads.sql. Apply after rls_0005_garage.sql.
-- NOT EXECUTED in this environment (no Supabase/PostgreSQL).
-- Players may READ their own diary read marks; every write goes through the control plane (service role), which records a
-- crew introduction only once the account's clears have opened it.
ALTER TABLE diary_reads ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON diary_reads FROM anon, authenticated;
GRANT SELECT ON diary_reads TO authenticated;
CREATE POLICY own_diary_reads_read ON diary_reads FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
GRANT ALL ON diary_reads TO service_role;
