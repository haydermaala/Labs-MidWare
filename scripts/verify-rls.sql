-- RLS enforcement gate. Run as the least-privileged runtime role (app_runtime,
-- NOSUPERUSER NOBYPASSRLS) against a database with the full migration chain applied.
--
-- Every check RAISEs on failure, so psql -v ON_ERROR_STOP=1 turns a regression into a
-- non-zero exit. This exists because an API-level cross-tenant test proves the
-- application's WHERE clause, not RLS — it passes identically with RLS disabled. The
-- only honest proof binds/unbinds the tenant GUC and observes row visibility here.
\set ON_ERROR_STOP on

DO $$
DECLARE
    forced       int;
    policies     int;
    is_super     bool;
    bypasses     bool;
    n            int;
    expected_forced CONSTANT int := 16;  -- P1's 10 + P3's 6
BEGIN
    -- 0. We must be testing as a role RLS actually applies to.
    SELECT rolsuper, rolbypassrls INTO is_super, bypasses
      FROM pg_roles WHERE rolname = current_user;
    IF is_super OR bypasses THEN
        RAISE EXCEPTION 'RLS gate must run as a NOSUPERUSER NOBYPASSRLS role; % is super=% bypass=%',
            current_user, is_super, bypasses;
    END IF;

    -- 1. Every tenant table is ENABLE + FORCE'd.
    SELECT count(*) INTO forced
      FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
     WHERE n.nspname = 'public' AND c.relforcerowsecurity;
    IF forced <> expected_forced THEN
        RAISE EXCEPTION 'expected % FORCE-RLS tables, found %', expected_forced, forced;
    END IF;

    SELECT count(*) INTO policies FROM pg_policies WHERE schemaname = 'public';
    IF policies < expected_forced THEN
        RAISE EXCEPTION 'expected at least % policies, found %', expected_forced, policies;
    END IF;

    -- 2. Fail-closed: with no tenant GUC bound, tenant tables reveal nothing.
    PERFORM set_config('app.tenant_id', '', false);
    FOR n IN SELECT 1 LOOP END LOOP;  -- no-op, keeps the block tidy

    SELECT count(*) INTO n FROM gateways;
    IF n <> 0 THEN RAISE EXCEPTION 'gateways leaked % rows with no tenant GUC', n; END IF;
    SELECT count(*) INTO n FROM scopes;
    IF n <> 0 THEN RAISE EXCEPTION 'scopes (P3) leaked % rows with no tenant GUC', n; END IF;
    SELECT count(*) INTO n FROM memberships;
    IF n <> 0 THEN RAISE EXCEPTION 'memberships leaked % rows with no tenant GUC', n; END IF;
    SELECT count(*) INTO n FROM audit;
    IF n <> 0 THEN RAISE EXCEPTION 'audit leaked % rows with no tenant GUC', n; END IF;

    -- 3. Bound to tenant A: sees A's rows, and ONLY A's.
    PERFORM set_config('app.tenant_id', 'ten_a', false);
    SELECT count(*) INTO n FROM gateways;
    IF n <> 1 THEN RAISE EXCEPTION 'tenant A expected 1 gateway, saw %', n; END IF;
    SELECT count(*) INTO n FROM gateways WHERE "TenantId" = 'ten_b';
    IF n <> 0 THEN RAISE EXCEPTION 'tenant A can see % of tenant B''s gateways', n; END IF;

    PERFORM set_config('app.tenant_id', 'ten_b', false);
    SELECT count(*) INTO n FROM gateways;
    IF n <> 1 THEN RAISE EXCEPTION 'tenant B expected 1 gateway, saw %', n; END IF;

    -- 4. WITH CHECK: a cross-tenant INSERT is refused.
    PERFORM set_config('app.tenant_id', 'ten_a', false);
    BEGIN
        INSERT INTO gateways ("Id","TenantId","Name","EnrolledAt","Active")
        VALUES ('gw_evil','ten_b','evil', now(), true);
        RAISE EXCEPTION 'cross-tenant INSERT succeeded — WITH CHECK is not enforcing';
    EXCEPTION WHEN insufficient_privilege OR check_violation THEN
        NULL;  -- expected
    END;

    -- 5. The auxiliary policies must be scoped to the commands they actually need.
    --    memberships_self_read / device_credentials_device_auth are read-only paths;
    --    left as FOR ALL they would (permissive policies being OR'd) let a caller
    --    write rows outside their tenant. See TightenAuxiliaryRlsPolicies.
    SELECT count(*) INTO n FROM pg_policies
     WHERE schemaname='public'
       AND policyname IN ('memberships_self_read','device_credentials_device_auth')
       AND cmd <> 'SELECT';
    IF n <> 0 THEN
        RAISE EXCEPTION '% read-only auxiliary policies are not scoped to SELECT', n;
    END IF;

    -- 6. memberships_self_read must not become a cross-tenant write vector.
    PERFORM set_config('app.tenant_id', 'ten_a', false);
    PERFORM set_config('app.user_id', 'usr_1', false);
    BEGIN
        INSERT INTO memberships ("Id","TenantId","UserId","Role","CreatedAt","Active")
        VALUES ('mem_evil','ten_b','usr_1','owner', now(), true);
        RAISE EXCEPTION 'self-read policy allowed a cross-tenant membership INSERT';
    EXCEPTION WHEN insufficient_privilege OR check_violation THEN
        NULL;  -- expected
    END;

    RAISE NOTICE 'RLS gate PASSED: % tables FORCE''d, % policies, fail-closed, isolated, write-checked',
        forced, policies;
END $$;
