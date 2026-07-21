// EF Core + PostgreSQL implementation of IControlPlaneStore (staging/production).
//
// A DbContext is not thread-safe, so this singleton creates a short-lived context
// per operation via IDbContextFactory. Every gateway/config/audit read is filtered
// by tenant id, preserving the same tenant-isolation guarantee as the in-memory store.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Relational, tenant-scoped control-plane store.</summary>
public sealed class EfControlPlaneStore : IControlPlaneStore
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;

    public EfControlPlaneStore(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    private void Audit(AppDbContext db, string kind, string tenantId, string detail) =>
        db.Audit.Add(new AuditEntity { At = _clock.GetUtcNow(), Kind = kind, TenantId = tenantId, Detail = detail });

    public Tenant CreateTenant(string name)
    {
        using var db = _factory.CreateDbContext();
        var entity = new TenantEntity { Id = Ids.New("ten"), Name = name, CreatedAt = _clock.GetUtcNow(), Active = true };
        // Scope to the NEW tenant's id: the `tenants` WITH CHECK policy requires
        // the inserted row's "Id" to equal app.tenant_id, and the audit row is
        // written under the same tenant.
        using var scope = TenantScope.Begin(db, entity.Id);
        db.Tenants.Add(entity);
        Audit(db, "tenant.created", entity.Id, entity.Name);
        db.SaveChanges();
        scope.Complete();
        return new Tenant(entity.Id, entity.Name, entity.CreatedAt, entity.Active);
    }

    // ── Tenant registry reads ────────────────────────────────────────────────
    // Tenants() enumerates across tenants, so it runs under a PlatformScope (the
    // app.platform flag + the tenants_platform_read policy — a cross-tenant read
    // of the registry only, ADR 0018 §7). The single-tenant lookups take a
    // specific id and stay tenant-scoped (the tenants self-policy), which is the
    // least-privilege choice — the platform flag is never set for them.

    public IReadOnlyCollection<Tenant> Tenants()
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformScope.Begin(db);
        var result = db.Tenants.AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .Select(t => new Tenant(t.Id, t.Name, t.CreatedAt, t.Active))
            .ToList();
        scope.Complete();
        return result;
    }

    public bool TenantExists(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var exists = db.Tenants.AsNoTracking().Any(t => t.Id == tenantId);
        scope.Complete();
        return exists;
    }

    public Tenant? FindTenant(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var tenant = db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new Tenant(t.Id, t.Name, t.CreatedAt, t.Active))
            .FirstOrDefault();
        scope.Complete();
        return tenant;
    }

    public Tenant? RenameTenant(string tenantId, string name)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var tenant = db.Tenants.FirstOrDefault(t => t.Id == tenantId);
        if (tenant is null)
        {
            return null;
        }
        tenant.Name = name;
        Audit(db, "tenant.renamed", tenantId, name);
        db.SaveChanges();
        scope.Complete();
        return new Tenant(tenant.Id, tenant.Name, tenant.CreatedAt, tenant.Active);
    }

    public bool DeactivateTenant(string tenantId) => SetTenantActive(tenantId, active: false);

    public bool ReactivateTenant(string tenantId) => SetTenantActive(tenantId, active: true);

    private bool SetTenantActive(string tenantId, bool active)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var tenant = db.Tenants.FirstOrDefault(t => t.Id == tenantId);
        if (tenant is null)
        {
            return false;
        }
        if (tenant.Active != active)
        {
            tenant.Active = active;
            Audit(db, active ? "tenant.reactivated" : "tenant.deactivated", tenantId, tenant.Name);
            db.SaveChanges();
        }
        scope.Complete();
        return true;
    }

    public bool DecommissionGateway(string tenantId, string gatewayId)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var gateway = db.Gateways.FirstOrDefault(g => g.Id == gatewayId && g.TenantId == tenantId);
        if (gateway is null)
        {
            return false;
        }
        if (gateway.Active)
        {
            gateway.Active = false;
            // Revoke the device credential so the gateway can no longer authenticate.
            // Visible under the tenant scope via the device_credentials gateway-join policy.
            var credential = db.DeviceCredentials.FirstOrDefault(c => c.GatewayId == gatewayId);
            if (credential is not null)
            {
                db.DeviceCredentials.Remove(credential);
            }
            Audit(db, "gateway.decommissioned", tenantId, gatewayId);
            db.SaveChanges();
        }
        scope.Complete();
        return true;
    }

    public BootstrapTokenView? IssueBootstrapToken(string tenantId, TimeSpan ttl)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        if (!db.Tenants.AsNoTracking().Any(t => t.Id == tenantId && t.Active))
        {
            return null;
        }
        var token = new BootstrapTokenEntity
        {
            Token = Ids.NewSecret(),
            TenantId = tenantId,
            ExpiresAt = _clock.GetUtcNow().Add(ttl),
            Used = false,
        };
        db.BootstrapTokens.Add(token);
        Audit(db, "enrollment.token_issued", tenantId, "bootstrap token issued");
        db.SaveChanges();
        scope.Complete();
        return new BootstrapTokenView(token.Token, token.ExpiresAt);
    }

    // ── Device-plane (ADR 0018 §6) ───────────────────────────────────────────
    // A gateway presents a secret before its tenant is known: a bootstrap token
    // (Enroll) or a gateway-id + device credential (ValidateDeviceCredential).
    // Reading that secret to validate it is what tenant RLS would block, so these
    // two open a DeviceScope that binds a device-auth GUC — the RLS policy reveals
    // only the row whose secret was presented. Once the tenant is resolved, the
    // steady-state ops (RecordHeartbeat/RecordTelemetry/CurrentConfig) run
    // tenant-scoped like everything else, taking the resolved tenant id.

    public EnrollmentResult? Enroll(string bootstrapToken, string gatewayName)
    {
        using var db = _factory.CreateDbContext();
        // Prove possession of the token: the bootstrap_tokens device-auth policy
        // reveals only the row whose "Token" equals app.device_token.
        using var scope = DeviceScope.ForEnrollment(db, bootstrapToken);
        // Track the token so consuming it is an optimistic-concurrency update: the
        // ConcurrencyToken column is in the UPDATE's WHERE clause, so if two callers
        // redeem the same token concurrently, the loser's SaveChanges affects zero
        // rows and throws — enforcing single-use at the database.
        var token = db.BootstrapTokens.FirstOrDefault(t => t.Token == bootstrapToken);
        if (token is null || token.Used || token.ExpiresAt <= _clock.GetUtcNow())
        {
            return null;
        }
        // Bind the resolved tenant so the tenant-active check and the inserts below
        // pass the tenant policies (and the token's own tenant row becomes visible).
        scope.BindTenant(token.TenantId);
        // A token issued before its tenant was deactivated must not still enroll.
        if (!db.Tenants.AsNoTracking().Any(t => t.Id == token.TenantId && t.Active))
        {
            return null;
        }

        token.Used = true;
        token.ConcurrencyToken = Ids.New("ct");
        var gateway = new GatewayEntity
        {
            Id = Ids.New("gw"),
            TenantId = token.TenantId,
            Name = gatewayName,
            EnrolledAt = _clock.GetUtcNow(),
            Active = true,
        };
        var credential = Ids.NewSecret();
        db.Gateways.Add(gateway);
        db.DeviceCredentials.Add(new DeviceCredentialEntity
        {
            GatewayId = gateway.Id,
            Credential = credential,
            TenantId = token.TenantId,
        });
        Audit(db, "gateway.enrolled", gateway.TenantId, gateway.Id);
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateConcurrencyException)
        {
            return null; // another caller consumed this token first (scope rolls back)
        }
        scope.Complete();
        return new EnrollmentResult(gateway.Id, gateway.TenantId, credential);
    }

    public IReadOnlyCollection<GatewayView> GatewaysFor(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var now = _clock.GetUtcNow();
        var result = db.Gateways.AsNoTracking()
            .Where(g => g.TenantId == tenantId)
            .OrderBy(g => g.EnrolledAt)
            .AsEnumerable() // status is derived against 'now'; compute after materializing
            .Select(g => new GatewayView(
                g.Id, g.TenantId, g.Name, g.EnrolledAt, g.Active, g.LastSeenAt,
                GatewayLiveness.Status(g.Active, g.LastSeenAt, now),
                new GatewayTelemetry(g.CapturedCount, g.PendingCount, g.DeliveredCount, g.DeadCount, g.LastCaptureAt)))
            .ToList();
        scope.Complete();
        return result;
    }

    public string? ValidateDeviceCredential(string gatewayId, string credential)
    {
        using var db = _factory.CreateDbContext();
        // The device_credentials device-auth policy reveals the row only when both
        // the gateway id and the credential match — so a stored secret is never
        // disclosed to a caller who knows only a gateway id. The row carries the
        // tenant, resolving the device-plane "session". The constant-time compare
        // stays as a second layer.
        using var scope = DeviceScope.ForCredential(db, gatewayId, credential);
        var row = db.DeviceCredentials.AsNoTracking()
            .Where(c => c.GatewayId == gatewayId)
            .Select(c => new { c.Credential, c.TenantId })
            .FirstOrDefault();
        scope.Complete();
        return row is not null && Ids.CredentialsEqual(row.Credential, credential) ? row.TenantId : null;
    }

    public bool RecordHeartbeat(string tenantId, string gatewayId)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var gateway = db.Gateways.FirstOrDefault(g => g.Id == gatewayId && g.TenantId == tenantId);
        if (gateway is null)
        {
            return false;
        }
        gateway.LastSeenAt = _clock.GetUtcNow();
        db.SaveChanges();
        scope.Complete();
        return true;
    }

    public bool RecordTelemetry(string tenantId, string gatewayId, GatewayTelemetry telemetry)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var gateway = db.Gateways.FirstOrDefault(g => g.Id == gatewayId && g.TenantId == tenantId);
        if (gateway is null)
        {
            return false;
        }
        gateway.CapturedCount = telemetry.Captured;
        gateway.PendingCount = telemetry.Pending;
        gateway.DeliveredCount = telemetry.Delivered;
        gateway.DeadCount = telemetry.Dead;
        gateway.LastCaptureAt = telemetry.LastCaptureAt;
        gateway.LastSeenAt = _clock.GetUtcNow();
        db.SaveChanges();
        scope.Complete();
        return true;
    }

    public string? TenantOfGateway(string gatewayId)
    {
        using var db = _factory.CreateDbContext();
        return db.Gateways.AsNoTracking()
            .Where(g => g.Id == gatewayId)
            .Select(g => g.TenantId)
            .FirstOrDefault();
    }

    public ConfigView? PublishConfig(string tenantId, string gatewayId, string settingsJson)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var ownerTenant = db.Gateways.AsNoTracking()
            .Where(g => g.Id == gatewayId)
            .Select(g => g.TenantId)
            .FirstOrDefault();
        if (ownerTenant != tenantId)
        {
            return null;
        }

        var existing = db.Configs.FirstOrDefault(c => c.GatewayId == gatewayId);
        if (existing is null)
        {
            existing = new ConfigEntity
            {
                GatewayId = gatewayId,
                TenantId = tenantId,
                Version = 1,
                Environment = "non-production",
                SettingsJson = settingsJson,
                PublishedAt = _clock.GetUtcNow(),
            };
            db.Configs.Add(existing);
        }
        else
        {
            existing.Version += 1;
            existing.SettingsJson = settingsJson;
            existing.PublishedAt = _clock.GetUtcNow();
        }
        Audit(db, "config.published", tenantId, $"{gatewayId} v{existing.Version}");
        db.SaveChanges();
        scope.Complete();
        return new ConfigView(gatewayId, existing.Version, existing.Environment, existing.SettingsJson, existing.PublishedAt);
    }

    public ConfigView? CurrentConfig(string tenantId, string gatewayId)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var c = db.Configs.AsNoTracking().FirstOrDefault(x => x.GatewayId == gatewayId && x.TenantId == tenantId);
        if (c is null)
        {
            return null;
        }
        Audit(db, "config.fetched", tenantId, gatewayId);
        db.SaveChanges();
        scope.Complete();
        return new ConfigView(gatewayId, c.Version, c.Environment, c.SettingsJson, c.PublishedAt);
    }

    public IReadOnlyCollection<AuditEvent> AuditFor(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        using var scope = TenantScope.Begin(db, tenantId);
        var result = db.Audit.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => new AuditEvent(e.At, e.Kind, e.TenantId, e.Detail))
            .ToList();
        scope.Complete();
        return result;
    }
}
