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

    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<UserSessionEntity> UserSessions => Set<UserSessionEntity>();
    public DbSet<UserTokenEntity> UserTokens => Set<UserTokenEntity>();
    public DbSet<RecoveryCodeEntity> RecoveryCodes => Set<RecoveryCodeEntity>();
    public DbSet<MembershipEntity> Memberships => Set<MembershipEntity>();
    public DbSet<InvitationEntity> Invitations => Set<InvitationEntity>();
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<GatewayEntity> Gateways => Set<GatewayEntity>();
    public DbSet<DeviceCredentialEntity> DeviceCredentials => Set<DeviceCredentialEntity>();
    public DbSet<BootstrapTokenEntity> BootstrapTokens => Set<BootstrapTokenEntity>();
    public DbSet<ConfigEntity> Configs => Set<ConfigEntity>();
    public DbSet<AuditEntity> Audit => Set<AuditEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>(e =>
        {
            e.ToTable("users");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Email).IsUnique();
        });

        modelBuilder.Entity<UserSessionEntity>(e =>
        {
            e.ToTable("user_sessions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<UserTokenEntity>(e =>
        {
            e.ToTable("user_tokens");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<RecoveryCodeEntity>(e =>
        {
            e.ToTable("recovery_codes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.UserId);
        });

        modelBuilder.Entity<MembershipEntity>(e =>
        {
            e.ToTable("memberships");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.UserId, x.TenantId }).IsUnique();
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<InvitationEntity>(e =>
        {
            e.ToTable("invitations");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => x.TenantId);
        });

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

/// <summary>An account holder. Email is stored normalized (trimmed, lower-case).
/// PasswordHash uses ASP.NET Core Identity's PBKDF2 v3 format.</summary>
public sealed class UserEntity
{
    public string Id { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? EmailVerifiedAt { get; set; }
    public bool Active { get; set; } = true;

    /// <summary>Base32 TOTP secret; set at MFA setup, armed once MfaEnabledAt is set.</summary>
    public string? MfaSecret { get; set; }
    public DateTimeOffset? MfaEnabledAt { get; set; }
}

/// <summary>A single-use MFA recovery code (only its SHA-256 hash is stored).</summary>
public sealed class RecoveryCodeEntity
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>A server-side session. Only the SHA-256 hash of the opaque token is
/// stored; the token itself is shown once at login.</summary>
public sealed class UserSessionEntity
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

/// <summary>A single-use, short-lived account token (email verification or
/// password reset). Only the SHA-256 hash of the token is stored.</summary>
public sealed class UserTokenEntity
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Purpose { get; set; } = ""; // "verify" | "reset"
    public string TokenHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}

/// <summary>A user's membership in a tenant, carrying exactly one baseline role.</summary>
public sealed class MembershipEntity
{
    public string Id { get; set; } = "";
    public string UserId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Role { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public bool Active { get; set; } = true;
}

/// <summary>A single-use email invitation into a tenant (hashed token).</summary>
public sealed class InvitationEntity
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "";
    public string TokenHash { get; set; } = "";
    public string CreatedByUserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? AcceptedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A tenant row.</summary>
public sealed class TenantEntity
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Inactive tenants are retained but cannot enroll new gateways.</summary>
    public bool Active { get; set; } = true;
}

/// <summary>An enrolled gateway row, scoped to a tenant.</summary>
public sealed class GatewayEntity
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTimeOffset EnrolledAt { get; set; }

    /// <summary>A decommissioned gateway is inactive and its credential revoked.</summary>
    public bool Active { get; set; } = true;

    /// <summary>Last authenticated contact (heartbeat/config fetch); null until first seen.</summary>
    public DateTimeOffset? LastSeenAt { get; set; }
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
