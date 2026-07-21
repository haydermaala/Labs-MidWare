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
    public DbSet<SubscriptionEntity> Subscriptions => Set<SubscriptionEntity>();
    public DbSet<BillingEventEntity> BillingEvents => Set<BillingEventEntity>();
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();
    public DbSet<GatewayEntity> Gateways => Set<GatewayEntity>();
    public DbSet<DeviceCredentialEntity> DeviceCredentials => Set<DeviceCredentialEntity>();
    public DbSet<BootstrapTokenEntity> BootstrapTokens => Set<BootstrapTokenEntity>();
    public DbSet<ConfigEntity> Configs => Set<ConfigEntity>();
    public DbSet<AuditEntity> Audit => Set<AuditEntity>();
    public DbSet<PermissionDefinitionEntity> PermissionDefinitions => Set<PermissionDefinitionEntity>();
    public DbSet<ScopeEntity> Scopes => Set<ScopeEntity>();
    public DbSet<RoleAssignmentEntity> RoleAssignments => Set<RoleAssignmentEntity>();
    public DbSet<SodRuleEntity> SodRules => Set<SodRuleEntity>();
    public DbSet<CustomRoleEntity> CustomRoles => Set<CustomRoleEntity>();
    public DbSet<RolePermissionEntity> RolePermissions => Set<RolePermissionEntity>();

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

        modelBuilder.Entity<SubscriptionEntity>(e =>
        {
            e.ToTable("subscriptions");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId).IsUnique();
        });

        modelBuilder.Entity<BillingEventEntity>(e =>
        {
            e.ToTable("billing_events");
            e.HasKey(x => x.Id);
            // Provider event ids are globally unique; this index makes webhook
            // processing idempotent (a duplicate delivery is a no-op).
            e.HasIndex(x => x.ProviderEventId).IsUnique();
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

        modelBuilder.Entity<PermissionDefinitionEntity>(e =>
        {
            // A global (non-tenant) reference table mirroring the code catalog
            // (Permissions.All), reconciled at startup by PermissionCatalogSync.
            e.ToTable("permission_definitions");
            e.HasKey(x => x.Key);
        });

        modelBuilder.Entity<ScopeEntity>(e =>
        {
            // A tenant's org-hierarchy nodes (P3). Path is the materialized ancestor
            // path (incl. self) for prefix descendant queries.
            e.ToTable("scopes");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.ParentId);
            e.HasIndex(x => x.Path);
        });

        modelBuilder.Entity<RoleAssignmentEntity>(e =>
        {
            // A subject's scoped, optionally-expiring role grant (P3).
            e.ToTable("role_assignments");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.ScopeId);
        });

        modelBuilder.Entity<SodRuleEntity>(e =>
        {
            // Per-tenant separation-of-duty rules (P3).
            e.ToTable("sod_rules");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<CustomRoleEntity>(e =>
        {
            // Tenant-defined roles (P3). (TenantId, RoleKey) uniqueness is enforced
            // in the (future) create-role service; single-column index here to avoid
            // a composite array that trips CA1861 in generated migrations.
            e.ToTable("custom_roles");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
        });

        modelBuilder.Entity<RolePermissionEntity>(e =>
        {
            // A custom role's granted permissions (P3).
            e.ToTable("role_permissions");
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

    /// <summary>When the user last proved their credentials for this session
    /// (login or step-up). Drives the fresh-auth window for high-risk permissions
    /// (ADR 0019).</summary>
    public DateTimeOffset LastAuthenticatedAt { get; set; }

    /// <summary>Whether MFA was completed for this session (login via MFA, or a
    /// step-up that included an MFA code).</summary>
    public bool MfaSatisfied { get; set; }
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

/// <summary>A tenant's subscription. Holds only provider ids and status — never
/// card data. Exactly one row per tenant.</summary>
public sealed class SubscriptionEntity
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string PlanId { get; set; } = Plans.Trial;
    public string Status { get; set; } = SubscriptionStatus.Trialing;
    public string? ProviderCustomerId { get; set; }
    public string? ProviderSubscriptionId { get; set; }
    public DateTimeOffset? CurrentPeriodEnd { get; set; }
    public bool CancelAtPeriodEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A processed provider webhook event, kept for idempotency + audit.</summary>
public sealed class BillingEventEntity
{
    public string Id { get; set; } = "";
    public string ProviderEventId { get; set; } = "";
    public string TenantId { get; set; } = "";
    public DateTimeOffset ReceivedAt { get; set; }
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

    // PHI-free telemetry — the gateway's last self-reported operational counters.
    // Message counts and timing only; never any message content or result value.
    public long CapturedCount { get; set; }
    public long PendingCount { get; set; }
    public long DeliveredCount { get; set; }
    public long DeadCount { get; set; }

    /// <summary>When the gateway last observed a message from the analyzer.</summary>
    public DateTimeOffset? LastCaptureAt { get; set; }
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

/// <summary>A row mirroring one <see cref="PermissionDefinition"/> from the code
/// catalog (Permissions.All), reconciled at startup. The code catalog is
/// authoritative; this table exists so the admin UI can list/annotate permissions
/// and so grants can reference them. Enum-valued fields are stored as their names.
/// <see cref="Active"/> is false when a permission was removed from the catalog.</summary>
public sealed class PermissionDefinitionEntity
{
    public string Key { get; set; } = "";
    public string Domain { get; set; } = "";
    public string Resource { get; set; } = "";
    public string Action { get; set; } = "";
    public string Risk { get; set; } = "";
    public string Capability { get; set; } = "";
    public bool RequiresMfa { get; set; }
    public bool RequiresFreshAuth { get; set; }
    public bool RequiresApproval { get; set; }
    public bool Delegable { get; set; }
    public string Description { get; set; } = "";
    public bool Active { get; set; } = true;
}

/// <summary>A node in a tenant's org hierarchy (P3): tenant → site → laboratory →
/// department. Mirrors <see cref="ScopeNode"/>. <see cref="Path"/> is the
/// materialized ancestor path (including self) for prefix descendant queries; the
/// root's <see cref="ParentId"/> is null.</summary>
public sealed class ScopeEntity
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Type { get; set; } = "";
    public string Name { get; set; } = "";
    public string? ParentId { get; set; }
    public string Path { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A subject's grant of a role at a scope (P3). Mirrors
/// <see cref="RoleAssignment"/>, plus provenance (<see cref="GrantedByUserId"/>)
/// and soft-revocation (<see cref="RevokedAt"/>). A null <see cref="ExpiresAt"/>
/// never expires; a set <see cref="RevokedAt"/> is inactive regardless.</summary>
public sealed class RoleAssignmentEntity
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string UserId { get; set; } = "";
    public string Role { get; set; } = "";
    public string ScopeId { get; set; } = "";
    public string GrantedByUserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>A per-tenant separation-of-duty rule (P3): no single subject may hold
/// both <see cref="PermissionA"/> and <see cref="PermissionB"/>. Mirrors
/// <see cref="SodRule"/>.</summary>
public sealed class SodRuleEntity
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Name { get; set; } = "";
    public string PermissionA { get; set; } = "";
    public string PermissionB { get; set; } = "";
    public bool Active { get; set; } = true;
}

/// <summary>A tenant-defined role (P3). <see cref="RoleKey"/> is unique within the
/// tenant and must not collide with a baseline <see cref="Roles"/> name; its granted
/// permissions are the <see cref="RolePermissionEntity"/> rows for the same key.</summary>
public sealed class CustomRoleEntity
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string RoleKey { get; set; } = "";
    public string Name { get; set; } = "";
    public string CreatedByUserId { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One permission granted to a custom role (P3).</summary>
public sealed class RolePermissionEntity
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string RoleKey { get; set; } = "";
    public string PermissionKey { get; set; } = "";
}
