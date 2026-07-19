// Control-plane API.
// Health + multi-tenant fleet management: tenants, secure gateway enrollment, and
// tenant-scoped gateway inventory. Management endpoints require an admin bearer
// token (from configuration); gateway enrollment authenticates with a single-use
// bootstrap token. No PHI, result values, or secrets appear in any payload here.
// PostgreSQL/EF Core and OIDC are wired in a later increment (in-memory for now).

using System.Reflection;
using System.Text.Json;
using ControlPlane.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ControlPlaneStore>();

var app = builder.Build();

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
var adminToken = app.Configuration["ControlPlane:AdminToken"];

bool IsAdmin(HttpRequest req) =>
    !string.IsNullOrEmpty(adminToken) &&
    req.Headers.Authorization.ToString() == $"Bearer {adminToken}";

// --- health ---------------------------------------------------------------
app.MapGet("/health", () => Results.Json(new HealthResponse("ok", "control-plane-api", version)));
app.MapGet("/health/ready", () => Results.Json(new HealthResponse("ready", "control-plane-api", version)));

// --- tenant management (admin) --------------------------------------------
app.MapPost("/api/tenants", (CreateTenantRequest body, ControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "name required" });
    var tenant = store.CreateTenant(body.Name.Trim());
    return Results.Created($"/api/tenants/{tenant.Id}", tenant);
});

app.MapGet("/api/tenants", (ControlPlaneStore store, HttpRequest req) =>
    IsAdmin(req) ? Results.Json(store.Tenants()) : Results.Unauthorized());

// Issue a short-lived, single-use bootstrap token an operator hands to a gateway.
app.MapPost("/api/tenants/{tenantId}/enrollment-tokens", (string tenantId, ControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    var token = store.IssueBootstrapToken(tenantId, TimeSpan.FromMinutes(15));
    return token is null ? Results.NotFound() : Results.Json(token);
});

// Tenant-scoped gateway inventory (never returns another tenant's gateways).
app.MapGet("/api/tenants/{tenantId}/gateways", (string tenantId, ControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!store.TenantExists(tenantId)) return Results.NotFound();
    return Results.Json(store.GatewaysFor(tenantId));
});

// Publish a (non-production) config version for a tenant's gateway.
app.MapPost("/api/tenants/{tenantId}/gateways/{gatewayId}/config",
    (string tenantId, string gatewayId, JsonElement settings, ControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    var view = store.PublishConfig(tenantId, gatewayId, settings.GetRawText());
    return view is null ? Results.NotFound() : Results.Json(view);
});

// Tenant audit log (admin).
app.MapGet("/api/tenants/{tenantId}/audit", (string tenantId, ControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!store.TenantExists(tenantId)) return Results.NotFound();
    return Results.Json(store.AuditFor(tenantId));
});

// --- gateway enrollment (bootstrap token is the credential) ----------------
app.MapPost("/api/gateways/enroll", (EnrollRequest body, ControlPlaneStore store) =>
{
    var result = store.Enroll(body.BootstrapToken, string.IsNullOrWhiteSpace(body.Name) ? "gateway" : body.Name.Trim());
    return result is null ? Results.Unauthorized() : Results.Json(result);
});

// A gateway fetches its own config, authenticated by its device credential.
app.MapGet("/api/gateways/config", (ControlPlaneStore store, HttpRequest req) =>
{
    var gatewayId = req.Headers["X-Gateway-Id"].ToString();
    var credential = req.Headers["X-Gateway-Credential"].ToString();
    if (string.IsNullOrEmpty(gatewayId) || !store.ValidateDeviceCredential(gatewayId, credential))
    {
        return Results.Unauthorized();
    }
    var config = store.CurrentConfig(gatewayId);
    return config is null ? Results.NoContent() : Results.Json(config);
});

app.Run();

/// <summary>Minimal, PHI-free health payload.</summary>
internal sealed record HealthResponse(string Status, string Service, string Version);

/// <summary>Request to create a tenant.</summary>
internal sealed record CreateTenantRequest(string Name);

/// <summary>Request to enroll a gateway using a bootstrap token.</summary>
internal sealed record EnrollRequest(string BootstrapToken, string? Name);

// Exposed so integration tests can host the app via WebApplicationFactory.
public partial class Program;
