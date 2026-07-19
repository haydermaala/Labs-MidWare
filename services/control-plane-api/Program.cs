// Control-plane API.
// Health + multi-tenant fleet management: tenants, secure gateway enrollment, and
// tenant-scoped gateway inventory. Management endpoints require an admin bearer
// token (from configuration); gateway enrollment authenticates with a single-use
// bootstrap token. No PHI, result values, or secrets appear in any payload here.
//
// Persistence is a deployment choice behind IControlPlaneStore: when a Postgres
// connection is configured (DATABASE_URL or ConnectionStrings:Postgres) the EF Core
// store is used and the schema is created on startup; otherwise an in-memory store
// backs local development and tests. OIDC is wired in a later increment.

using System.Reflection;
using System.Text.Json;
using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton(TimeProvider.System);

var postgres = DatabaseConfig.ResolveConnectionString(builder.Configuration);
if (postgres is not null)
{
    builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(postgres));
    builder.Services.AddSingleton<IControlPlaneStore, EfControlPlaneStore>();
}
else
{
    builder.Services.AddSingleton<IControlPlaneStore, InMemoryControlPlaneStore>();
}

var app = builder.Build();

// Apply EF Core migrations on startup when running against Postgres, so the schema
// is created and kept current in a versioned, auditable way. SchemaBootstrap also
// adopts a database created by the earlier EnsureCreated (baselining it) so this
// deploy is a clean no-op. Startup migration suits a single-replica staging deploy;
// a multi-replica or regulated production rollout should move this to a gated
// release step (see ADR 0013).
if (postgres is not null)
{
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    SchemaBootstrap.Apply(db);
}

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
var adminToken = app.Configuration["ControlPlane:AdminToken"];

bool IsAdmin(HttpRequest req) =>
    !string.IsNullOrEmpty(adminToken) &&
    req.Headers.Authorization.ToString() == $"Bearer {adminToken}";

// --- health ---------------------------------------------------------------
app.MapGet("/health", () => Results.Json(new HealthResponse("ok", "control-plane-api", version)));
app.MapGet("/health/ready", () => Results.Json(new HealthResponse("ready", "control-plane-api", version)));

// --- tenant management (admin) --------------------------------------------
app.MapPost("/api/tenants", (CreateTenantRequest body, IControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "name required" });
    var tenant = store.CreateTenant(body.Name.Trim());
    return Results.Created($"/api/tenants/{tenant.Id}", tenant);
});

app.MapGet("/api/tenants", (IControlPlaneStore store, HttpRequest req) =>
    IsAdmin(req) ? Results.Json(store.Tenants()) : Results.Unauthorized());

// Deactivate a tenant (soft): stops new enrollment; data and audit retained.
app.MapPost("/api/tenants/{tenantId}/deactivate", (string tenantId, IControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    return store.DeactivateTenant(tenantId) ? Results.NoContent() : Results.NotFound();
});

// Reactivate a previously deactivated tenant.
app.MapPost("/api/tenants/{tenantId}/reactivate", (string tenantId, IControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    return store.ReactivateTenant(tenantId) ? Results.NoContent() : Results.NotFound();
});

// Issue a short-lived, single-use bootstrap token an operator hands to a gateway.
app.MapPost("/api/tenants/{tenantId}/enrollment-tokens", (string tenantId, IControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    var token = store.IssueBootstrapToken(tenantId, TimeSpan.FromMinutes(15));
    return token is null ? Results.NotFound() : Results.Json(token);
});

// Tenant-scoped gateway inventory (never returns another tenant's gateways).
app.MapGet("/api/tenants/{tenantId}/gateways", (string tenantId, IControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!store.TenantExists(tenantId)) return Results.NotFound();
    return Results.Json(store.GatewaysFor(tenantId));
});

// Decommission a gateway within a tenant: mark inactive and revoke its credential.
app.MapPost("/api/tenants/{tenantId}/gateways/{gatewayId}/decommission",
    (string tenantId, string gatewayId, IControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    return store.DecommissionGateway(tenantId, gatewayId) ? Results.NoContent() : Results.NotFound();
});

// Publish a (non-production) config version for a tenant's gateway.
app.MapPost("/api/tenants/{tenantId}/gateways/{gatewayId}/config",
    (string tenantId, string gatewayId, JsonElement settings, IControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    var view = store.PublishConfig(tenantId, gatewayId, settings.GetRawText());
    return view is null ? Results.NotFound() : Results.Json(view);
});

// Tenant audit log (admin).
app.MapGet("/api/tenants/{tenantId}/audit", (string tenantId, IControlPlaneStore store, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!store.TenantExists(tenantId)) return Results.NotFound();
    return Results.Json(store.AuditFor(tenantId));
});

// --- gateway enrollment (bootstrap token is the credential) ----------------
app.MapPost("/api/gateways/enroll", (EnrollRequest body, IControlPlaneStore store) =>
{
    var result = store.Enroll(body.BootstrapToken, string.IsNullOrWhiteSpace(body.Name) ? "gateway" : body.Name.Trim());
    return result is null ? Results.Unauthorized() : Results.Json(result);
});

// A gateway fetches its own config, authenticated by its device credential.
app.MapGet("/api/gateways/config", (IControlPlaneStore store, HttpRequest req) =>
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
