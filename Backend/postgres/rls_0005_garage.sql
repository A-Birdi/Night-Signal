-- Night Signal: RLS for migrations/postgres/0005_garage.sql. Apply after rls_0004_toys.sql.
-- NOT EXECUTED in this environment (no Supabase/PostgreSQL).
-- Players may READ their own car instances, part ownership, workspaces and settled Buy-and-Apply records; every write goes
-- through the control plane (service role). Issued quotes are server-only (the client refers to them by id).
ALTER TABLE car_instances            ENABLE ROW LEVEL SECURITY;
ALTER TABLE car_part_ownership       ENABLE ROW LEVEL SECURITY;
ALTER TABLE car_workspaces           ENABLE ROW LEVEL SECURITY;
ALTER TABLE garage_quotes            ENABLE ROW LEVEL SECURITY;
ALTER TABLE garage_quote_settlements ENABLE ROW LEVEL SECURITY;

REVOKE ALL ON car_instances, car_part_ownership, car_workspaces, garage_quotes, garage_quote_settlements FROM anon, authenticated;
GRANT SELECT ON car_instances, car_part_ownership, car_workspaces, garage_quote_settlements TO authenticated;

CREATE POLICY own_car_instances_read   ON car_instances            FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_part_ownership_read  ON car_part_ownership       FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_car_workspaces_read  ON car_workspaces           FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);
CREATE POLICY own_quote_settlements_read ON garage_quote_settlements FOR SELECT TO authenticated USING (account_id = (SELECT auth.uid())::text);

GRANT ALL ON car_instances, car_part_ownership, car_workspaces, garage_quotes, garage_quote_settlements TO service_role;
