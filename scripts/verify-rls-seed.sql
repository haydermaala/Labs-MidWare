-- Seed the minimum fixture the RLS gate asserts against: two tenants, one gateway each.
-- Run as the OWNER (it must bypass/relax RLS to plant cross-tenant rows), BEFORE
-- scripts/verify-rls.sql runs as app_runtime.
\set ON_ERROR_STOP on

-- The owner may itself be subject to FORCE; drop FORCE for the seed, then restore.
ALTER TABLE tenants  NO FORCE ROW LEVEL SECURITY;
ALTER TABLE gateways NO FORCE ROW LEVEL SECURITY;

INSERT INTO tenants ("Id","Name","CreatedAt","Active","Offboarded","Status")
VALUES ('ten_a','Tenant A', now(), true, false, 'Active'),
       ('ten_b','Tenant B', now(), true, false, 'Active')
ON CONFLICT ("Id") DO NOTHING;

INSERT INTO gateways ("Id","TenantId","Name","EnrolledAt","Active")
VALUES ('gw_a','ten_a','edge-a', now(), true),
       ('gw_b','ten_b','edge-b', now(), true)
ON CONFLICT ("Id") DO NOTHING;

ALTER TABLE tenants  FORCE ROW LEVEL SECURITY;
ALTER TABLE gateways FORCE ROW LEVEL SECURITY;
