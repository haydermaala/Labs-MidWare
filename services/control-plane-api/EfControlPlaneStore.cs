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

    // ── Platform / cross-tenant reads (NOT tenant-scoped) ────────────────────
    // These enumerate or probe across tenants and so cannot run under a single
    // tenant GUC. They are invoked only by the platform-admin surface (today the
    // AdminToken). Before FORCE RLS is enabled on the live DB, they must run via
    // the platform-elevated path (P6: named platform roles behind /platform-admin,
    // whose DB role has BYPASSRLS or a dedicated cross-tenant policy). Until then
    // the app connects as the owner role, for which the migration is not yet
    // merged (ADR 0018 §Rollout), so no behaviour changes.

    public IReadOnlyCollection<Tenant> Tenants()
    {
        using var db = _factory.CreateDbContext();
        return db.Tenants.AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .Select(t => new Tenant(t.Id, t.Name, t.CreatedAt, t.Active))
            .ToList();
    }

    public bool TenantExists(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        return db.Tenants.AsNoTracking().Any(t => t.Id == tenantId);
    }

    public Tenant? FindTenant(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        return db.Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId)
            .Select(t => new Tenant(t.Id, t.Name, t.CreatedAt, t.Active))
            .FirstOrDefault();
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

    // ── Device-plane (NOT tenant-scoped yet) ─────────────────────────────────
    // Enroll, ValidateDeviceCredential, RecordHeartbeat, RecordTelemetry,
    // TenantOfGateway and CurrentConfig are reached by a gateway that presents a
    // bootstrap token or a gateway-id + device credential — the tenant is not
    // known until *after* that secret is validated, but reading the token/
    // credential/gateway to validate it is exactly what tenant RLS would block
    // (chicken-and-egg). These need a bounded device-auth path before FORCE RLS:
    // a narrow policy permitting a by-primary-key + secret lookup on
    // bootstrap_tokens / device_credentials / gateways with no tenant context,
    // after which the operation sets app.tenant_id to the resolved tenant for any
    // writes. Designed and applied in the staged rollout (ADR 0018 §Rollout);
    // shadow-mode logging surfaces any device path that still lacks context.

    public EnrollmentResult? Enroll(string bootstrapToken, string gatewayName)
    {
        using var db = _factory.CreateDbContext();
        // Track the token so consuming it is an optimistic-concurrency update: the
        // ConcurrencyToken column is in the UPDATE's WHERE clause, so if two callers
        // redeem the same token concurrently, the loser's SaveChanges affects zero
        // rows and throws — enforcing single-use at the database.
        var token = db.BootstrapTokens.FirstOrDefault(t => t.Token == bootstrapToken);
        if (token is null || token.Used || token.ExpiresAt <= _clock.GetUtcNow())
        {
            return null;
        }
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
        db.DeviceCredentials.Add(new DeviceCredentialEntity { GatewayId = gateway.Id, Credential = credential });
        Audit(db, "gateway.enrolled", gateway.TenantId, gateway.Id);
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateConcurrencyException)
        {
            return null; // another caller consumed this token first
        }
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

    public bool ValidateDeviceCredential(string gatewayId, string credential)
    {
        using var db = _factory.CreateDbContext();
        var stored = db.DeviceCredentials.AsNoTracking()
            .Where(c => c.GatewayId == gatewayId)
            .Select(c => c.Credential)
            .FirstOrDefault();
        return stored is not null && Ids.CredentialsEqual(stored, credential);
    }

    public bool RecordHeartbeat(string gatewayId)
    {
        using var db = _factory.CreateDbContext();
        var gateway = db.Gateways.FirstOrDefault(g => g.Id == gatewayId);
        if (gateway is null)
        {
            return false;
        }
        gateway.LastSeenAt = _clock.GetUtcNow();
        db.SaveChanges();
        return true;
    }

    public bool RecordTelemetry(string gatewayId, GatewayTelemetry telemetry)
    {
        using var db = _factory.CreateDbContext();
        var gateway = db.Gateways.FirstOrDefault(g => g.Id == gatewayId);
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

    public ConfigView? CurrentConfig(string gatewayId)
    {
        using var db = _factory.CreateDbContext();
        var c = db.Configs.AsNoTracking().FirstOrDefault(x => x.GatewayId == gatewayId);
        if (c is null)
        {
            return null;
        }
        Audit(db, "config.fetched", c.TenantId, gatewayId);
        db.SaveChanges();
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
