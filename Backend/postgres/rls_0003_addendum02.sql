-- Night Signal: RLS for migrations/postgres/0003_addendum02.sql. dormant_rooms is server-only (it holds rejoin grants
-- for other accounts): row-level security on, no client privilege or policy at all. Apply after rls_0002_addendum01.sql.
-- NOT EXECUTED in this environment (no Supabase/PostgreSQL).
ALTER TABLE dormant_rooms ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON dormant_rooms FROM anon, authenticated;
GRANT ALL ON dormant_rooms TO service_role;
