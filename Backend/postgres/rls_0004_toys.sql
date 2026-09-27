-- Night Signal: RLS for migrations/postgres/0004_toys.sql. toy_snapshots is server-only (it holds every member's toy
-- seats and a convoy's shared artwork): row-level security on, no client privilege or policy at all. Apply after
-- rls_0003_addendum02.sql. NOT EXECUTED in this environment (no Supabase/PostgreSQL).
ALTER TABLE toy_snapshots ENABLE ROW LEVEL SECURITY;
REVOKE ALL ON toy_snapshots FROM anon, authenticated;
GRANT ALL ON toy_snapshots TO service_role;
