// The control-plane store contract, implemented by an in-memory store (dev/tests)
// and an EF Core + PostgreSQL store (staging/production). Endpoints depend on the
// interface, so the persistence backend is a deployment choice.

using System.Security.Cryptography;

namespace ControlPlane.Api;

/// <summary>Tenant + gateway + enrollment persistence, tenant-scoped by design.</summary>
public interface IControlPlaneStore
{
    /// <summary>Create a tenant.</summary>
    Tenant CreateTenant(string name);

    /// <summary>All tenants, oldest first.</summary>
    IReadOnlyCollection<Tenant> Tenants();

    /// <summary>Whether a tenant exists (active or deactivated).</summary>
    bool TenantExists(string tenantId);

    /// <summary>
    /// Deactivate a tenant (soft): it stops issuing enrollment tokens and enrolling
    /// gateways, but its gateways, config, and audit trail are retained. No-op if
    /// already inactive. Returns false if the tenant does not exist.
    /// </summary>
    bool DeactivateTenant(string tenantId);

    /// <summary>Reactivate a previously deactivated tenant. Returns false if unknown.</summary>
    bool ReactivateTenant(string tenantId);

    /// <summary>
    /// Decommission a gateway within a tenant: mark it inactive and revoke its device
    /// credential so it can no longer authenticate or fetch config. Irreversible — a
    /// returning device must re-enroll for a fresh credential. The gateway row and its
    /// audit history are retained. Returns false if the gateway is not in that tenant.
    /// </summary>
    bool DecommissionGateway(string tenantId, string gatewayId);

    /// <summary>Issue a short-lived, single-use bootstrap token for an active tenant.</summary>
    BootstrapTokenView? IssueBootstrapToken(string tenantId, TimeSpan ttl);

    /// <summary>Redeem a bootstrap token for a new gateway + device credential.</summary>
    EnrollmentResult? Enroll(string bootstrapToken, string gatewayName);

    /// <summary>Gateways for a tenant (never returns another tenant's gateways).</summary>
    IReadOnlyCollection<GatewayView> GatewaysFor(string tenantId);

    /// <summary>Validate a gateway's device credential.</summary>
    bool ValidateDeviceCredential(string gatewayId, string credential);

    /// <summary>
    /// Record that a gateway was just seen (heartbeat / authenticated contact),
    /// updating its last-seen time. Returns false if the gateway does not exist.
    /// </summary>
    bool RecordHeartbeat(string gatewayId);

    /// <summary>The tenant that owns a gateway, if any.</summary>
    string? TenantOfGateway(string gatewayId);

    /// <summary>Publish a new (non-production) config version for a tenant's gateway.</summary>
    ConfigView? PublishConfig(string tenantId, string gatewayId, string settingsJson);

    /// <summary>The current config for a gateway, or null.</summary>
    ConfigView? CurrentConfig(string gatewayId);

    /// <summary>Append-only audit events for a tenant, oldest first.</summary>
    IReadOnlyCollection<AuditEvent> AuditFor(string tenantId);
}

/// <summary>Shared id/secret generation.</summary>
internal static class Ids
{
    public static string New(string prefix) => $"{prefix}_{Guid.NewGuid():N}";

    public static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public static bool CredentialsEqual(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(a),
            System.Text.Encoding.UTF8.GetBytes(b));
}
