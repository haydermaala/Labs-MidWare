// Control-plane domain: tenants, gateways, and secure enrollment.
//
// Multi-tenant by construction: gateways belong to a tenant and are only ever
// returned within their tenant's scope. Enrollment uses a short-lived, single-use
// bootstrap token which the gateway redeems for a rotated device credential — no
// long-lived shared secret, and the gateway needs no inbound port.
//
// This is an in-memory implementation for development and tests. PostgreSQL +
// EF Core and OIDC authentication are wired in a later increment (OPEN); admin
// endpoints here use a simple bearer token from configuration.

using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace ControlPlane.Api;

/// <summary>A tenant (customer/organization).</summary>
public sealed record Tenant(string Id, string Name, DateTimeOffset CreatedAt);

/// <summary>An enrolled gateway, scoped to a tenant.</summary>
public sealed record Gateway(string Id, string TenantId, string Name, DateTimeOffset EnrolledAt);

/// <summary>Public view of a gateway (no credential).</summary>
public sealed record GatewayView(string Id, string TenantId, string Name, DateTimeOffset EnrolledAt);

/// <summary>Result of enrolling: the gateway id and its (one-time shown) device credential.</summary>
public sealed record EnrollmentResult(string GatewayId, string TenantId, string DeviceCredential);

internal sealed record BootstrapToken(string Token, string TenantId, DateTimeOffset ExpiresAt, bool Used);

/// <summary>Thread-safe, tenant-scoped in-memory store.</summary>
public sealed class ControlPlaneStore
{
    private readonly ConcurrentDictionary<string, Tenant> _tenants = new();
    private readonly ConcurrentDictionary<string, Gateway> _gateways = new();
    private readonly ConcurrentDictionary<string, string> _deviceCredentials = new(); // gatewayId -> credential
    private readonly ConcurrentDictionary<string, BootstrapToken> _bootstrap = new();
    private readonly ConcurrentDictionary<string, ConfigRecord> _configs = new(); // gatewayId -> config
    private readonly ConcurrentQueue<AuditEvent> _audit = new();

    private readonly TimeProvider _clock;

    public ControlPlaneStore(TimeProvider clock) => _clock = clock;

    private void Audit(string kind, string tenantId, string detail) =>
        _audit.Enqueue(new AuditEvent(_clock.GetUtcNow(), kind, tenantId, detail));

    /// <summary>Append-only audit events for a tenant, oldest first.</summary>
    public IReadOnlyCollection<AuditEvent> AuditFor(string tenantId) =>
        _audit.Where(e => e.TenantId == tenantId).OrderBy(e => e.At).ToList();

    private static string NewId(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    private static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public Tenant CreateTenant(string name)
    {
        var tenant = new Tenant(NewId("ten"), name, _clock.GetUtcNow());
        _tenants[tenant.Id] = tenant;
        Audit("tenant.created", tenant.Id, tenant.Name);
        return tenant;
    }

    public IReadOnlyCollection<Tenant> Tenants() => _tenants.Values.OrderBy(t => t.CreatedAt).ToList();

    public bool TenantExists(string tenantId) => _tenants.ContainsKey(tenantId);

    /// <summary>Issue a short-lived, single-use bootstrap token for a tenant.</summary>
    public BootstrapTokenView? IssueBootstrapToken(string tenantId, TimeSpan ttl)
    {
        if (!TenantExists(tenantId))
        {
            return null;
        }
        var token = new BootstrapToken(NewSecret(), tenantId, _clock.GetUtcNow().Add(ttl), Used: false);
        _bootstrap[token.Token] = token;
        Audit("enrollment.token_issued", tenantId, "bootstrap token issued");
        return new BootstrapTokenView(token.Token, token.ExpiresAt);
    }

    /// <summary>
    /// Redeem a bootstrap token: create a gateway and return a rotated device
    /// credential. The token is single-use and time-bounded.
    /// </summary>
    public EnrollmentResult? Enroll(string bootstrapToken, string gatewayName)
    {
        if (!_bootstrap.TryGetValue(bootstrapToken, out var token))
        {
            return null;
        }
        if (token.Used || token.ExpiresAt <= _clock.GetUtcNow())
        {
            return null;
        }
        // Mark used atomically; reject if someone else won the race.
        var consumed = token with { Used = true };
        if (!_bootstrap.TryUpdate(bootstrapToken, consumed, token))
        {
            return null;
        }

        var gateway = new Gateway(NewId("gw"), token.TenantId, gatewayName, _clock.GetUtcNow());
        _gateways[gateway.Id] = gateway;
        var credential = NewSecret();
        _deviceCredentials[gateway.Id] = credential;
        Audit("gateway.enrolled", gateway.TenantId, gateway.Id);
        return new EnrollmentResult(gateway.Id, gateway.TenantId, credential);
    }

    /// <summary>Gateways for a tenant — tenant-scoped; never returns other tenants' gateways.</summary>
    public IReadOnlyCollection<GatewayView> GatewaysFor(string tenantId) =>
        _gateways.Values
            .Where(g => g.TenantId == tenantId)
            .OrderBy(g => g.EnrolledAt)
            .Select(g => new GatewayView(g.Id, g.TenantId, g.Name, g.EnrolledAt))
            .ToList();

    /// <summary>Validate a gateway's device credential (used by gateway calls).</summary>
    public bool ValidateDeviceCredential(string gatewayId, string credential) =>
        _deviceCredentials.TryGetValue(gatewayId, out var stored) &&
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(stored),
            System.Text.Encoding.UTF8.GetBytes(credential));

    /// <summary>The tenant that owns a gateway, if it exists.</summary>
    public string? TenantOfGateway(string gatewayId) =>
        _gateways.TryGetValue(gatewayId, out var gw) ? gw.TenantId : null;

    /// <summary>
    /// Publish a new config version for a gateway within a tenant. Config is
    /// always marked non-production here — production config requires a separate,
    /// approved pipeline. Returns null if the gateway is not in that tenant.
    /// </summary>
    public ConfigView? PublishConfig(string tenantId, string gatewayId, string settingsJson)
    {
        if (TenantOfGateway(gatewayId) != tenantId)
        {
            return null;
        }
        var next = _configs.AddOrUpdate(
            gatewayId,
            _ => new ConfigRecord(1, "non-production", settingsJson, _clock.GetUtcNow(), tenantId),
            (_, existing) => existing with
            {
                Version = existing.Version + 1,
                SettingsJson = settingsJson,
                PublishedAt = _clock.GetUtcNow(),
            });
        Audit("config.published", tenantId, $"{gatewayId} v{next.Version}");
        return new ConfigView(gatewayId, next.Version, next.Environment, next.SettingsJson, next.PublishedAt);
    }

    /// <summary>The current config for a gateway, or null if none published.</summary>
    public ConfigView? CurrentConfig(string gatewayId)
    {
        if (!_configs.TryGetValue(gatewayId, out var c))
        {
            return null;
        }
        Audit("config.fetched", c.TenantId, gatewayId);
        return new ConfigView(gatewayId, c.Version, c.Environment, c.SettingsJson, c.PublishedAt);
    }
}

internal sealed record ConfigRecord(int Version, string Environment, string SettingsJson, DateTimeOffset PublishedAt, string TenantId);

/// <summary>A published gateway config (non-production).</summary>
public sealed record ConfigView(string GatewayId, int Version, string Environment, string SettingsJson, DateTimeOffset PublishedAt);

/// <summary>An append-only audit event.</summary>
public sealed record AuditEvent(DateTimeOffset At, string Kind, string TenantId, string Detail);

/// <summary>A bootstrap token as returned to the operator (token shown once).</summary>
public sealed record BootstrapTokenView(string Token, DateTimeOffset ExpiresAt);
