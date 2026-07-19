// EF Core persistence for the control plane. Entities mirror the domain records
// in Enrollment.cs; the mapping between the two lives in EfControlPlaneStore.
//
// Tenant isolation is enforced in the store's queries (every gateway/config/audit
// read is filtered by tenant); the schema keeps tenant ids as foreign keys so the
// relationship is explicit and indexable.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Relational store for tenants, gateways, credentials, tokens, config, audit.</summary>
public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<GatewayEntity> Gateways => Set<GatewayEntity>();
    public DbSet<DeviceCredentialEntity> DeviceCredentials => Set<DeviceCredentialEntity>();
    public DbSet<BootstrapTokenEntity> BootstrapTokens => Set<BootstrapTokenEntity>();
    public DbSet<ConfigEntity> Configs => Set<ConfigEntity>();
    public DbSet<AuditEntity> Audit => Set<AuditEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<TenantEntity>(e =>
        {
            e.ToTable("tenants");
            e.HasKey(x => x.Id);
        });

        modelBuilder.Entity<GatewayEntity>(e =>
        {
            e.ToTable("gateways");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<DeviceCredentialEntity>(e =>
        {
            e.ToTable("device_credentials");
            e.HasKey(x => x.GatewayId);
        });

        modelBuilder.Entity<BootstrapTokenEntity>(e =>
        {
            e.ToTable("bootstrap_tokens");
            e.HasKey(x => x.Token);
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.ConcurrencyToken).IsConcurrencyToken();
        });

        modelBuilder.Entity<ConfigEntity>(e =>
        {
            e.ToTable("configs");
            e.HasKey(x => x.GatewayId);
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<AuditEntity>(e =>
        {
            e.ToTable("audit");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
        });
    }
}

/// <summary>A tenant row.</summary>
public sealed class TenantEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>An enrolled gateway row, scoped to a tenant.</summary>
public sealed class GatewayEntity
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset EnrolledAt { get; set; }
}

/// <summary>A gateway's rotated device credential.</summary>
public sealed class DeviceCredentialEntity
{
    public string GatewayId { get; set; } = "";
    public string Credential { get; set; } = "";
}

/// <summary>A short-lived, single-use bootstrap token.</summary>
public sealed class BootstrapTokenEntity
{
    public string Token { get; set; } = "";
    public string TenantId { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public bool Used { get; set; }

    /// <summary>Optimistic-concurrency guard so a token is redeemable only once.</summary>
    public string ConcurrencyToken { get; set; } = "";
}

/// <summary>The current published (non-production) config for a gateway.</summary>
public sealed class ConfigEntity
{
    public string GatewayId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public int Version { get; set; }
    public string Environment { get; set; } = "non-production";
    public string SettingsJson { get; set; } = "";
    public DateTimeOffset PublishedAt { get; set; }
}

/// <summary>An append-only audit event row.</summary>
public sealed class AuditEntity
{
    public long Id { get; set; }
    public DateTimeOffset At { get; set; }
    public string Kind { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Detail { get; set; } = "";
}
