// Control-plane domain: tenants, gateways, and secure enrollment.
//
// Multi-tenant by construction: gateways belong to a tenant and are only ever
// returned within their tenant's scope. Enrollment uses a short-lived, single-use
// bootstrap token which the gateway redeems for a rotated device credential — no
// long-lived shared secret, and the gateway needs no inbound port.
//
// This is the in-memory implementation of IControlPlaneStore, used for development
// and tests. The EF Core + PostgreSQL implementation (EfControlPlaneStore) is the
// deployment backend, selected by configuration. OIDC authentication is wired in a
// later increment (OPEN); admin endpoints here use a bearer token from config.

using System.Collections.Concurrent;

namespace ControlPlane.Api;

/// <summary>A tenant (customer/organization). Inactive tenants are retained but
/// cannot enroll new gateways.</summary>
public sealed record Tenant(string Id, string Name, DateTimeOffset CreatedAt, bool Active);

/// <summary>PHI-free operational counters a gateway self-reports. These are
/// message *counts* and timing only — never any message content or result value.
/// Captured = messages observed from the analyzer; the rest mirror the edge
/// outbox (pending awaiting delivery, delivered downstream, dead-lettered).</summary>
public sealed record GatewayTelemetry(
    long Captured, long Pending, long Delivered, long Dead, DateTimeOffset? LastCaptureAt)
{
    /// <summary>The zero snapshot for a gateway that has reported nothing yet.</summary>
    public static readonly GatewayTelemetry Empty = new(0, 0, 0, 0, null);
}

/// <summary>An enrolled gateway, scoped to a tenant.</summary>
public sealed record Gateway(
    string Id, string TenantId, string Name, DateTimeOffset EnrolledAt, bool Active,
    DateTimeOffset? LastSeenAt, GatewayTelemetry Telemetry);

/// <summary>Public view of a gateway (no credential). A decommissioned gateway has
/// Active=false and its credential revoked. <see cref="Status"/> is a derived
/// liveness label from <see cref="LastSeenAt"/>. Telemetry is the last self-report.</summary>
public sealed record GatewayView(
    string Id, string TenantId, string Name, DateTimeOffset EnrolledAt,
    bool Active, DateTimeOffset? LastSeenAt, string Status, GatewayTelemetry Telemetry);

/// <summary>Derives a gateway's liveness label. Liveness is never persisted — it is
/// computed from the last-seen time against a staleness window at read time.</summary>
public static class GatewayLiveness
{
    /// <summary>A gateway seen within this window is considered online.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    /// <summary>"decommissioned" | "never" | "online" | "offline".</summary>
    public static string Status(bool active, DateTimeOffset? lastSeenAt, DateTimeOffset now) =>
        !active ? "decommissioned"
        : lastSeenAt is null ? "never"
        : now - lastSeenAt.Value <= Timeout ? "online"
        : "offline";
}

/// <summary>Result of enrolling: the gateway id and its (one-time shown) device credential.</summary>
public sealed record EnrollmentResult(string GatewayId, string TenantId, string DeviceCredential);

internal sealed record BootstrapToken(string Token, string TenantId, DateTimeOffset ExpiresAt, bool Used);

/// <summary>Thread-safe, tenant-scoped in-memory store (dev/tests default).</summary>
public sealed class InMemoryControlPlaneStore : IControlPlaneStore
{
    private readonly ConcurrentDictionary<string, Tenant> _tenants = new();
    private readonly ConcurrentDictionary<string, Gateway> _gateways = new();
    private readonly ConcurrentDictionary<string, string> _deviceCredentials = new(); // gatewayId -> credential
    private readonly ConcurrentDictionary<string, BootstrapToken> _bootstrap = new();
    private readonly ConcurrentDictionary<string, ConfigRecord> _configs = new(); // gatewayId -> config
    private readonly ConcurrentQueue<AuditEvent> _audit = new();

    private readonly TimeProvider _clock;

    public InMemoryControlPlaneStore(TimeProvider clock) => _clock = clock;

    private void Audit(string kind, string tenantId, string detail) =>
        _audit.Enqueue(new AuditEvent(_clock.GetUtcNow(), kind, tenantId, detail));

    /// <summary>Append-only audit events for a tenant, oldest first.</summary>
    public IReadOnlyCollection<AuditEvent> AuditFor(string tenantId) =>
        _audit.Where(e => e.TenantId == tenantId).OrderBy(e => e.At).ToList();

    public Tenant CreateTenant(string name)
    {
        var tenant = new Tenant(Ids.New("ten"), name, _clock.GetUtcNow(), Active: true);
        _tenants[tenant.Id] = tenant;
        Audit("tenant.created", tenant.Id, tenant.Name);
        return tenant;
    }

    public IReadOnlyCollection<Tenant> Tenants() => _tenants.Values.OrderBy(t => t.CreatedAt).ToList();

    public bool TenantExists(string tenantId) => _tenants.ContainsKey(tenantId);

    public Tenant? FindTenant(string tenantId) =>
        _tenants.TryGetValue(tenantId, out var t) ? t : null;

    public Tenant? RenameTenant(string tenantId, string name)
    {
        if (!_tenants.TryGetValue(tenantId, out var tenant))
        {
            return null;
        }
        var renamed = tenant with { Name = name };
        _tenants[tenantId] = renamed;
        Audit("tenant.renamed", tenantId, name);
        return renamed;
    }

    private bool TenantIsActive(string tenantId) =>
        _tenants.TryGetValue(tenantId, out var t) && t.Active;

    public bool DeactivateTenant(string tenantId) => SetTenantActive(tenantId, active: false);

    public bool ReactivateTenant(string tenantId) => SetTenantActive(tenantId, active: true);

    private bool SetTenantActive(string tenantId, bool active)
    {
        if (!_tenants.TryGetValue(tenantId, out var tenant))
        {
            return false;
        }
        if (tenant.Active != active)
        {
            _tenants[tenantId] = tenant with { Active = active };
            Audit(active ? "tenant.reactivated" : "tenant.deactivated", tenantId, tenant.Name);
        }
        return true;
    }

    public bool DecommissionGateway(string tenantId, string gatewayId)
    {
        if (!_gateways.TryGetValue(gatewayId, out var gateway) || gateway.TenantId != tenantId)
        {
            return false;
        }
        if (gateway.Active)
        {
            _gateways[gatewayId] = gateway with { Active = false };
            // Revoke the device credential so the gateway can no longer authenticate.
            _deviceCredentials.TryRemove(gatewayId, out _);
            Audit("gateway.decommissioned", tenantId, gatewayId);
        }
        return true;
    }

    /// <summary>Issue a short-lived, single-use bootstrap token for an active tenant.</summary>
    public BootstrapTokenView? IssueBootstrapToken(string tenantId, TimeSpan ttl)
    {
        if (!TenantIsActive(tenantId))
        {
            return null;
        }
        var token = new BootstrapToken(Ids.NewSecret(), tenantId, _clock.GetUtcNow().Add(ttl), Used: false);
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
        // A token issued before its tenant was deactivated must not still enroll.
        if (!TenantIsActive(token.TenantId))
        {
            return null;
        }
        // Mark used atomically; reject if someone else won the race.
        var consumed = token with { Used = true };
        if (!_bootstrap.TryUpdate(bootstrapToken, consumed, token))
        {
            return null;
        }

        var gateway = new Gateway(
            Ids.New("gw"), token.TenantId, gatewayName, _clock.GetUtcNow(),
            Active: true, LastSeenAt: null, Telemetry: GatewayTelemetry.Empty);
        _gateways[gateway.Id] = gateway;
        var credential = Ids.NewSecret();
        _deviceCredentials[gateway.Id] = credential;
        Audit("gateway.enrolled", gateway.TenantId, gateway.Id);
        return new EnrollmentResult(gateway.Id, gateway.TenantId, credential);
    }

    /// <summary>Gateways for a tenant — tenant-scoped; never returns other tenants' gateways.</summary>
    public IReadOnlyCollection<GatewayView> GatewaysFor(string tenantId)
    {
        var now = _clock.GetUtcNow();
        return _gateways.Values
            .Where(g => g.TenantId == tenantId)
            .OrderBy(g => g.EnrolledAt)
            .Select(g => new GatewayView(
                g.Id, g.TenantId, g.Name, g.EnrolledAt, g.Active, g.LastSeenAt,
                GatewayLiveness.Status(g.Active, g.LastSeenAt, now), g.Telemetry))
            .ToList();
    }

    /// <summary>Validate a gateway's device credential; return its tenant if valid,
    /// else null (used by gateway calls to resolve the device-plane tenant).</summary>
    public string? ValidateDeviceCredential(string gatewayId, string credential) =>
        _deviceCredentials.TryGetValue(gatewayId, out var stored) &&
        Ids.CredentialsEqual(stored, credential) &&
        _gateways.TryGetValue(gatewayId, out var gateway)
            ? gateway.TenantId
            : null;

    /// <summary>Record a gateway heartbeat (updates last-seen). Tenant-scoped.</summary>
    public bool RecordHeartbeat(string tenantId, string gatewayId)
    {
        if (!_gateways.TryGetValue(gatewayId, out var gateway) || gateway.TenantId != tenantId)
        {
            return false;
        }
        _gateways[gatewayId] = gateway with { LastSeenAt = _clock.GetUtcNow() };
        return true;
    }

    /// <summary>Record a gateway's PHI-free telemetry snapshot (also counts as a
    /// heartbeat — a telemetry report proves the gateway is alive). Tenant-scoped.</summary>
    public bool RecordTelemetry(string tenantId, string gatewayId, GatewayTelemetry telemetry)
    {
        if (!_gateways.TryGetValue(gatewayId, out var gateway) || gateway.TenantId != tenantId)
        {
            return false;
        }
        _gateways[gatewayId] = gateway with { LastSeenAt = _clock.GetUtcNow(), Telemetry = telemetry };
        return true;
    }

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

    /// <summary>The current config for a gateway within a tenant, or null if none
    /// published (or it belongs to another tenant). Tenant-scoped.</summary>
    public ConfigView? CurrentConfig(string tenantId, string gatewayId)
    {
        if (!_configs.TryGetValue(gatewayId, out var c) || c.TenantId != tenantId)
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
