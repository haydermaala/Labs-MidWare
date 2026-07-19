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
        var entity = new TenantEntity { Id = Ids.New("ten"), Name = name, CreatedAt = _clock.GetUtcNow() };
        db.Tenants.Add(entity);
        Audit(db, "tenant.created", entity.Id, entity.Name);
        db.SaveChanges();
        return new Tenant(entity.Id, entity.Name, entity.CreatedAt);
    }

    public IReadOnlyCollection<Tenant> Tenants()
    {
        using var db = _factory.CreateDbContext();
        return db.Tenants.AsNoTracking()
            .OrderBy(t => t.CreatedAt)
            .Select(t => new Tenant(t.Id, t.Name, t.CreatedAt))
            .ToList();
    }

    public bool TenantExists(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        return db.Tenants.AsNoTracking().Any(t => t.Id == tenantId);
    }

    public BootstrapTokenView? IssueBootstrapToken(string tenantId, TimeSpan ttl)
    {
        using var db = _factory.CreateDbContext();
        if (!db.Tenants.AsNoTracking().Any(t => t.Id == tenantId))
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
        return new BootstrapTokenView(token.Token, token.ExpiresAt);
    }

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

        token.Used = true;
        token.ConcurrencyToken = Ids.New("ct");
        var gateway = new GatewayEntity
        {
            Id = Ids.New("gw"),
            TenantId = token.TenantId,
            Name = gatewayName,
            EnrolledAt = _clock.GetUtcNow(),
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
        return db.Gateways.AsNoTracking()
            .Where(g => g.TenantId == tenantId)
            .OrderBy(g => g.EnrolledAt)
            .Select(g => new GatewayView(g.Id, g.TenantId, g.Name, g.EnrolledAt))
            .ToList();
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
        return db.Audit.AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.At).ThenBy(e => e.Id)
            .Select(e => new AuditEvent(e.At, e.Kind, e.TenantId, e.Detail))
            .ToList();
    }
}
