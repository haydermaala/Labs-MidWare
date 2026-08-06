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

// CORS for the browser-based operator console. Locked down by default: only the
// origins named in ControlPlane:AllowedOrigins (comma-separated) may call the API,
// and only the headers/methods this API actually uses. No credentials — auth is a
// bearer token, not cookies. With no configured origins, cross-origin is blocked.
// The allowlist is evaluated per request against live configuration.
var configuration = builder.Configuration;
static string[] AllowedOrigins(IConfiguration config) =>
    (config["ControlPlane:AllowedOrigins"] ?? string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy
        .SetIsOriginAllowed(origin =>
            AllowedOrigins(configuration).Contains(origin, StringComparer.OrdinalIgnoreCase))
        .WithHeaders("Authorization", "Content-Type")
        .WithMethods("GET", "POST")));

// The application database backs identity (always) and the fleet store (when
// Postgres is configured). Without DATABASE_URL, the EF in-memory provider keeps
// local/dev/tests database-free while auth still exercises the same code path.
var postgres = DatabaseConfig.ResolveConnectionString(builder.Configuration);
if (postgres is not null)
{
    builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseNpgsql(postgres));
    builder.Services.AddSingleton<IControlPlaneStore, EfControlPlaneStore>();
}
else
{
    builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseInMemoryDatabase("labconnect-dev"));
    builder.Services.AddSingleton<IControlPlaneStore, InMemoryControlPlaneStore>();
}
builder.Services.AddSingleton<AuthService>();
builder.Services.AddSingleton<MembershipService>();
builder.Services.AddSingleton<BillingService>();
// Central authorization engine (P2, ADR 0019) — the authoritative gate for
// tenant-scoped endpoints. The scope-aware layer (P3, ADR 0020) resolves a
// subject's effective roles at a target scope before delegating to it.
builder.Services.AddSingleton<IAuthorizationEngine, AuthorizationEngine>();
builder.Services.AddSingleton<IScopedAuthorizationEngine, ScopedAuthorizationEngine>();
builder.Services.AddSingleton<ScopeService>();
builder.Services.AddSingleton<RoleGrantService>();
builder.Services.AddSingleton<ApprovalService>();
// Platform super-admin (P6): named-role authorization + assignment persistence,
// disjoint from tenant roles. Endpoints + god-mode-token retirement are the next slice.
builder.Services.AddSingleton<PlatformAdminService>();
builder.Services.AddSingleton<PlatformSupportService>();
builder.Services.AddSingleton<PlatformAuditService>();
builder.Services.AddSingleton<PlatformOffboardService>();
builder.Services.AddSingleton<IPlatformAuthorizationEngine, PlatformAuthorizationEngine>();

// Billing provider: Stripe when a secret key is configured (Phase E3),
// otherwise a deterministic fake for dev/tests and unconfigured environments.
if (!string.IsNullOrEmpty(builder.Configuration["Stripe:SecretKey"]))
{
    builder.Services.AddSingleton<IBillingProvider, StripeBillingProvider>();
}
else
{
    builder.Services.AddSingleton<IBillingProvider, FakeBillingProvider>();
}

// Email: Titan SMTP when configured (Smtp:Host), else a dev/test sink.
if (!string.IsNullOrEmpty(builder.Configuration["Smtp:Host"]))
{
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();
}
else
{
    builder.Services.AddSingleton<IEmailSender, NullEmailSender>();
}

// Credential-guessing defenses: a tight per-IP fixed window on login attempts.
// ControlPlane:LoginRatePermit overrides the default (ops tuning + tests); it is
// resolved per request from live configuration, like the CORS allowlist.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("login", ctx =>
    {
        var config = ctx.RequestServices.GetRequiredService<IConfiguration>();
        var permit = int.TryParse(config["ControlPlane:LoginRatePermit"], out var p) ? p : 10;
        return System.Threading.RateLimiting.RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new System.Threading.RateLimiting.FixedWindowRateLimiterOptions
            {
                PermitLimit = permit,
                Window = TimeSpan.FromMinutes(1),
            });
    });
});

var app = builder.Build();

// Apply EF Core migrations on startup when running against Postgres, so the schema
// is created and kept current in a versioned, auditable way. SchemaBootstrap also
// adopts a database created by the earlier EnsureCreated (baselining it) so this
// deploy is a clean no-op. Startup migration suits a single-replica staging deploy;
// a multi-replica or regulated production rollout should move this to a gated
// release step (see ADR 0013).
if (postgres is not null)
{
    // Migrations need DDL/owner rights. Under RLS the runtime factory connects as
    // a least-privilege role that cannot ALTER TABLE / CREATE POLICY, so the
    // startup migration uses a dedicated migration connection (owner role) when one
    // is configured, falling back to the runtime connection otherwise (ADR 0018
    // §Rollout). This is the only place that connects for DDL.
    var migrationConn = DatabaseConfig.ResolveMigrationConnectionString(app.Configuration);
    var migrationOptions = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(migrationConn).Options;
    using var db = new AppDbContext(migrationOptions);
    SchemaBootstrap.Apply(db);
    // Mirror the code permission catalog into permission_definitions (ADR 0019).
    PermissionCatalogSync.Apply(db);
    // Seed tenant-root role assignments from existing memberships (ADR 0020) so the
    // scope-aware engine can later be wired in without any member losing access. Runs
    // on the migration (owner) connection above, which bypasses FORCE RLS — required
    // because the backfill is cross-tenant (see docs/architecture/p3-rls-premerge.md).
    MembershipAssignmentBackfill.Apply(db, app.Services.GetRequiredService<TimeProvider>());
}

// Security response headers on every response. This service serves both the JSON
// API and the single-page operator console (same origin), so the CSP is scoped to
// what the SPA needs and no more: everything from 'self', data: images, and inline
// styles (the design system injects its stylesheet as an inline <style>). No
// inline scripts, no external origins, and framing is denied. HSTS hardens the
// public HTTPS endpoint (Railway terminates TLS in front of the app); browsers
// ignore HSTS over plain http, so it is safe to send unconditionally.
const string csp =
    "default-src 'self'; " +
    "base-uri 'self'; " +
    "object-src 'none'; " +
    "frame-ancestors 'none'; " +
    "img-src 'self' data:; " +
    "style-src 'self' 'unsafe-inline'; " +
    "script-src 'self'; " +
    "connect-src 'self'; " +
    "font-src 'self'";
app.Use(async (ctx, next) =>
{
    var headers = ctx.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["X-Frame-Options"] = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Content-Security-Policy"] = csp;
    headers["Strict-Transport-Security"] = "max-age=63072000; includeSubDomains";
    await next();
});

// Serve the built operator console (SPA) from wwwroot: real files (index.html,
// hashed JS/CSS assets) are served directly; any unmatched non-API route falls
// back to index.html so client-side routing works. API + health endpoints are
// matched first, so they are unaffected.
app.UseDefaultFiles();
app.UseStaticFiles();

app.UseCors();
app.UseRateLimiter();

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
var adminToken = app.Configuration["ControlPlane:AdminToken"];

// The synthetic actor recorded when the god-mode token acts. It has no user identity,
// so every action it takes collapses to this one string in the audit trail — which is
// itself an argument for retiring it in favour of named platform roles.
const string PlatformAdminActor = "platform-admin";

// Scope-aware authorization — the gate for tenant-scoped endpoints (ADR 0020,
// layered over the P2 engine of ADR 0019). Every tenant-scoped endpoint is
// authorized at the tenant ROOT scope: the caller's membership role is synthesized
// as a root assignment (so today's tenant-wide access is preserved exactly), then
// unioned with their persisted root-level grants (so explicit grants can add
// access). A finer target scope per resource is a later slice.
var scopedEngine = app.Services.GetRequiredService<IScopedAuthorizationEngine>();
var scopeService = app.Services.GetRequiredService<ScopeService>();
var roleGrants = app.Services.GetRequiredService<RoleGrantService>();
var authzClock = app.Services.GetRequiredService<TimeProvider>();

// Platform (super-admin) authorization — disjoint from tenant authz (P6, ADR §8).
var platformEngine = app.Services.GetRequiredService<IPlatformAuthorizationEngine>();
var platformAdmin = app.Services.GetRequiredService<PlatformAdminService>();
var platformSupport = app.Services.GetRequiredService<PlatformSupportService>();
var platformAudit = app.Services.GetRequiredService<PlatformAuditService>();
var platformOffboard = app.Services.GetRequiredService<PlatformOffboardService>();

bool IsAdmin(HttpRequest req) =>
    !string.IsNullOrEmpty(adminToken) &&
    req.Headers.Authorization.ToString() == $"Bearer {adminToken}";

// --- health ---------------------------------------------------------------
// Liveness: the process is up (no dependencies checked).
app.MapGet("/health", () => Results.Json(new HealthResponse("ok", "control-plane-api", version)));

// Readiness: verifies the database is reachable, so an orchestrator never routes
// traffic to (or completes a rollout onto) a replica that cannot serve requests.
// Returns 503 when the DB is unreachable.
//
// CRITICAL: a missing or mistyped DATABASE_URL does NOT surface as an error — the
// app falls back to the EF in-memory provider, whose CanConnectAsync() returns true.
// Readiness would then be green while every tenant silently reads an EMPTY database.
// Outside Development that fallback is never legitimate, so it is reported not-ready
// and named in the payload. The cutover runbook's "/health/ready green" step depends
// on this distinction.
var onInMemoryFallback = postgres is null;
var fallbackIsFatal = onInMemoryFallback && !app.Environment.IsDevelopment();
if (fallbackIsFatal)
{
    StartupLog.InMemoryFallbackInNonDevelopment(app.Logger, app.Environment.EnvironmentName);
}

app.MapGet("/health/ready", async (IDbContextFactory<AppDbContext> factory) =>
{
    if (fallbackIsFatal)
    {
        return Results.Json(
            new HealthResponse("not-ready", "control-plane-api", version, "in-memory (DATABASE_URL not configured)"),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    var provider = onInMemoryFallback ? "in-memory" : "postgres";
    try
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Database.CanConnectAsync()
            ? Results.Json(new HealthResponse("ready", "control-plane-api", version, provider))
            : Results.Json(new HealthResponse("not-ready", "control-plane-api", version, provider),
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Json(new HealthResponse("not-ready", "control-plane-api", version, provider),
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

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

// A tenant's general settings (any member of the tenant may read).
app.MapGet("/api/tenants/{tenantId}/settings", (string tenantId, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.TenantSettingsView) is { } denied) return denied;
    var tenant = store.FindTenant(tenantId);
    return tenant is null ? Results.NotFound() : Results.Json(tenant);
});

// Rename a tenant (owner only). Name is trimmed and length-bounded.
app.MapPost("/api/tenants/{tenantId}/rename", (string tenantId, RenameTenantRequest body, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.TenantRename) is { } denied) return denied;
    var name = (body.Name ?? string.Empty).Trim();
    if (name.Length is < 2 or > 120)
    {
        return Results.BadRequest(new { error = "name must be 2 to 120 characters" });
    }
    var tenant = store.RenameTenant(tenantId, name);
    return tenant is null ? Results.NotFound() : Results.Json(tenant);
});

// Deactivate a tenant (soft): stops new enrollment; data and audit retained.
app.MapPost("/api/tenants/{tenantId}/deactivate", (string tenantId, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.TenantDeactivate) is { } denied) return denied;
    return store.DeactivateTenant(tenantId) ? Results.NoContent() : Results.NotFound();
});

// Reactivate a previously deactivated tenant.
app.MapPost("/api/tenants/{tenantId}/reactivate", (string tenantId, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.TenantReactivate) is { } denied) return denied;
    return store.ReactivateTenant(tenantId) ? Results.NoContent() : Results.NotFound();
});

// Issue a short-lived, single-use bootstrap token an operator hands to a gateway.
app.MapPost("/api/tenants/{tenantId}/enrollment-tokens", (string tenantId, IControlPlaneStore store, AuthService auth, MembershipService members, BillingService billing, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.FleetGatewayEnroll) is { } denied) return denied;
    // Entitlement enforced server-side: only active gateways count toward quota.
    var activeGateways = store.GatewaysFor(tenantId).Count(g => g.Active);
    if (!billing.CanAddGateway(tenantId, activeGateways))
    {
        var plan = billing.EntitlementsFor(tenantId);
        return Results.Json(new
        {
            error = "gateway quota reached for the current plan",
            planId = plan.PlanId,
            gatewayQuota = plan.GatewayQuota,
        }, statusCode: StatusCodes.Status402PaymentRequired);
    }
    var token = store.IssueBootstrapToken(tenantId, TimeSpan.FromMinutes(15));
    return token is null ? Results.NotFound() : Results.Json(token);
});

// Tenant-scoped gateway inventory (never returns another tenant's gateways).
app.MapGet("/api/tenants/{tenantId}/gateways", (string tenantId, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.FleetGatewayView) is { } denied) return denied;
    if (!store.TenantExists(tenantId)) return Results.NotFound();
    return Results.Json(store.GatewaysFor(tenantId));
});

// Decommission a gateway within a tenant: mark inactive and revoke its credential.
// Authorized at the gateway's own org scope (P3), so a scoped grant reaches only
// its own gateways.
app.MapPost("/api/tenants/{tenantId}/gateways/{gatewayId}/decommission",
    (string tenantId, string gatewayId, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.FleetGatewayDecommission,
        store.GatewayScope(tenantId, gatewayId)) is { } denied) return denied;
    return store.DecommissionGateway(tenantId, gatewayId) ? Results.NoContent() : Results.NotFound();
});

// Publish a (non-production) config version for a tenant's gateway (at its scope).
app.MapPost("/api/tenants/{tenantId}/gateways/{gatewayId}/config",
    (string tenantId, string gatewayId, JsonElement settings, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.FleetConfigPublish,
        store.GatewayScope(tenantId, gatewayId)) is { } denied) return denied;
    var view = store.PublishConfig(tenantId, gatewayId, settings.GetRawText());
    return view is null ? Results.NotFound() : Results.Json(view);
});

// Pin a gateway to an org scope (or clear it to tenant-wide). Tenant-level fleet
// management, so authorized at the root.
app.MapPost("/api/tenants/{tenantId}/gateways/{gatewayId}/scope",
    (string tenantId, string gatewayId, AssignScopeRequest body, IControlPlaneStore store, ScopeService scopes, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.FleetGatewayEnroll) is { } denied) return denied;
    if (body.ScopeId is not null && scopes.Tree(tenantId)?.Find(body.ScopeId) is null)
    {
        return Results.BadRequest(new { error = "unknown scope in this tenant" });
    }
    return store.AssignGatewayScope(tenantId, gatewayId, body.ScopeId)
        ? Results.NoContent()
        : Results.NotFound();
});

// Tenant audit log (any member role; platform admin).
app.MapGet("/api/tenants/{tenantId}/audit", (string tenantId, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.AuditLogView) is { } denied) return denied;
    if (!store.TenantExists(tenantId)) return Results.NotFound();
    return Results.Json(store.AuditFor(tenantId));
});

// --- memberships + invitations (Phase C3) ----------------------------------
app.MapGet("/api/me/memberships", (AuthService auth, MembershipService members, HttpRequest req) =>
{
    var current = CurrentUser(req, auth);
    return current is null
        ? Results.Unauthorized()
        : Results.Json(members.MembershipsFor(current.Value.User.Id));
});

// Platform-admin bootstrap: grant a membership directly (first owner of a tenant).
app.MapPost("/api/admin/memberships", (GrantMembershipRequest body, MembershipService members, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    return members.Grant(body.UserId, body.TenantId, body.Role)
        ? Results.NoContent()
        : Results.BadRequest(new { error = "unknown user, tenant, or role" });
});

app.MapGet("/api/tenants/{tenantId}/members", (string tenantId, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersMemberView) is { } denied) return denied;
    return Results.Json(members.MembersOf(tenantId));
});

// The actor's role drives the owner-only guards below; the platform admin token
// acts with owner authority.
string ActorRole(HttpRequest req, AuthService auth, MembershipService members, string tenantId)
{
    if (IsAdmin(req))
    {
        return Roles.Owner;
    }
    var current = CurrentUser(req, auth);
    return current is null ? "" : members.RoleIn(current.Value.User.Id, tenantId) ?? "";
}

IResult ChangeOutcome(MembershipService.ChangeResult result) => result switch
{
    MembershipService.ChangeResult.Ok => Results.NoContent(),
    MembershipService.ChangeResult.NotFound => Results.NotFound(),
    MembershipService.ChangeResult.InvalidRole => Results.BadRequest(new { error = "unknown role" }),
    MembershipService.ChangeResult.LastOwner => Results.Conflict(new
    {
        error = "a laboratory must keep at least one owner; promote another member first",
    }),
    _ => Results.StatusCode(StatusCodes.Status403Forbidden),
};

app.MapPost("/api/tenants/{tenantId}/members/{userId}/role",
    (string tenantId, string userId, ChangeRoleRequest body, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersMemberChangeRole) is { } denied) return denied;
    return ChangeOutcome(members.ChangeRole(tenantId, userId, body.Role, ActorRole(req, auth, members, tenantId)));
});

app.MapPost("/api/tenants/{tenantId}/members/{userId}/remove",
    (string tenantId, string userId, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersMemberRemove) is { } denied) return denied;
    return ChangeOutcome(members.RemoveMember(tenantId, userId, ActorRole(req, auth, members, tenantId)));
});

app.MapPost("/api/tenants/{tenantId}/invitations",
    async (string tenantId, InviteRequest body, AuthService auth, MembershipService members, IEmailSender mail, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersMemberInvite) is { } denied) return denied;
    // Inviting an owner is the same privilege grant as promoting one.
    if (body.Role == Roles.Owner && ActorRole(req, auth, members, tenantId) != Roles.Owner)
    {
        return Results.StatusCode(StatusCodes.Status403Forbidden);
    }
    var byUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    var created = members.Invite(tenantId, body.Email, body.Role, byUserId);
    if (created is null)
    {
        return Results.BadRequest(new { error = "valid email and a known role are required" });
    }
    // The invitation is already durable; delivery is reported, not fatal, so a
    // mail outage does not leave the admin unsure whether it was created.
    var delivered = await MailDelivery.TrySendAsync(mail,
        EmailTemplates.Invitation(created.View.Email, created.TenantName, created.View.Role,
            Link("/invite", created.Token)),
        "invitation", app.Logger);
    return Results.Created(
        $"/api/tenants/{tenantId}/invitations/{created.View.Id}",
        new InvitationCreatedResponse(created.View, delivered));
}).RequireRateLimiting("login");

app.MapGet("/api/tenants/{tenantId}/invitations", (string tenantId, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersInvitationView) is { } denied) return denied;
    return Results.Json(members.InvitationsFor(tenantId));
});

app.MapPost("/api/tenants/{tenantId}/invitations/{invitationId}/revoke",
    (string tenantId, string invitationId, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersInvitationRevoke) is { } denied) return denied;
    return members.RevokeInvitation(tenantId, invitationId) ? Results.NoContent() : Results.NotFound();
});

// Accept as the signed-in user; the invitation email must match the account.
app.MapPost("/api/invitations/accept", (TokenRequest body, AuthService auth, MembershipService members, HttpRequest req) =>
{
    var current = CurrentUser(req, auth);
    if (current is null)
    {
        return Results.Unauthorized();
    }
    var membership = members.Accept(body.Token, current.Value.User.Id);
    return membership is null
        ? Results.BadRequest(new { error = "invalid, expired, or mismatched invitation" })
        : Results.Json(membership);
});

// --- P3: scope hierarchy (ADR 0020) ----------------------------------------
// The tenant org tree (tenant → site → laboratory → department) that scoped role
// assignments are granted against. Reading is any member; shaping the structure
// is a tenant-management action. A dedicated scope permission is a later refinement.
app.MapGet("/api/tenants/{tenantId}/scopes",
    (string tenantId, AuthService auth, MembershipService members, ScopeService scopes, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.TenantSettingsView) is { } denied) return denied;
    return Results.Json(scopes.List(tenantId));
});

app.MapPost("/api/tenants/{tenantId}/scopes",
    (string tenantId, CreateScopeRequest body, AuthService auth, MembershipService members, ScopeService scopes, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.TenantRename) is { } denied) return denied;
    if (string.IsNullOrWhiteSpace(body.Name))
    {
        return Results.BadRequest(new { error = "a scope name is required" });
    }
    // No parent → the tenant root (idempotent, always type Tenant).
    if (string.IsNullOrWhiteSpace(body.ParentId))
    {
        var root = scopes.EnsureRoot(tenantId, body.Name);
        return Results.Created($"/api/tenants/{tenantId}/scopes/{root.Id}",
            new ScopeView(root.Id, root.Type, root.Name, root.ParentId, root.Path));
    }
    if (!Enum.TryParse<ScopeType>(body.Type, ignoreCase: true, out var type))
    {
        return Results.BadRequest(new { error = "unknown scope type" });
    }
    var child = scopes.CreateChild(tenantId, body.ParentId, type, body.Name);
    return child is null
        ? Results.BadRequest(new { error = "unknown parent, or invalid nesting for the type" })
        : Results.Created($"/api/tenants/{tenantId}/scopes/{child.Id}",
            new ScopeView(child.Id, child.Type, child.Name, child.ParentId, child.Path));
});

// --- P3: scoped role grants + custom roles (ADR 0020) ----------------------
// A grant is a role held at a scope (role_assignments). Creating/revoking one is
// a member-admin action (same permission as changing a role) and is bounded by
// delegation limits + separation-of-duty in RoleGrantService. The scope-aware
// engine consuming these assignments is a later slice; these endpoints only
// author the data.
IResult GrantOutcomeResult(string tenantId, RoleGrantService.GrantResult r) => r.Outcome switch
{
    RoleGrantService.GrantOutcome.Ok =>
        Results.Created($"/api/tenants/{tenantId}/role-assignments/{r.Assignment!.Id}", r.Assignment),
    RoleGrantService.GrantOutcome.UnknownScope =>
        Results.BadRequest(new { error = "unknown scope in this tenant" }),
    RoleGrantService.GrantOutcome.UnknownRole =>
        Results.BadRequest(new { error = "unknown role" }),
    RoleGrantService.GrantOutcome.DelegationDenied =>
        Results.Json(new { error = "you cannot delegate these permissions", permissions = r.Offending },
            statusCode: StatusCodes.Status403Forbidden),
    RoleGrantService.GrantOutcome.SodViolation =>
        Results.Conflict(new { error = "a separation-of-duty rule would be violated", rules = r.Offending }),
    _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
};

IResult CustomRoleOutcomeResult(string tenantId, RoleGrantService.CustomRoleResult r) => r.Outcome switch
{
    RoleGrantService.CustomRoleOutcome.Ok =>
        Results.Created($"/api/tenants/{tenantId}/custom-roles/{r.Role!.RoleKey}", r.Role),
    RoleGrantService.CustomRoleOutcome.ReservedRoleKey =>
        Results.BadRequest(new { error = "role key collides with a baseline role" }),
    RoleGrantService.CustomRoleOutcome.RoleKeyTaken =>
        Results.Conflict(new { error = "a custom role with this key already exists" }),
    RoleGrantService.CustomRoleOutcome.NoValidPermissions =>
        Results.BadRequest(new { error = "at least one known permission key is required" }),
    RoleGrantService.CustomRoleOutcome.DelegationDenied =>
        Results.Json(new { error = "you cannot delegate these permissions", permissions = r.Offending },
            statusCode: StatusCodes.Status403Forbidden),
    _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
};

app.MapGet("/api/tenants/{tenantId}/role-assignments",
    (string tenantId, string? userId, AuthService auth, MembershipService members, RoleGrantService grants, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersMemberView) is { } denied) return denied;
    return Results.Json(grants.AssignmentsFor(tenantId, userId));
});

app.MapPost("/api/tenants/{tenantId}/role-assignments",
    (string tenantId, GrantRoleRequest body, AuthService auth, MembershipService members, RoleGrantService grants, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersMemberChangeRole) is { } denied) return denied;
    var grantorUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    return GrantOutcomeResult(tenantId, grants.Grant(
        tenantId, grantorUserId, ActorRole(req, auth, members, tenantId),
        body.UserId, body.Role, body.ScopeId, body.ExpiresAt));
});

app.MapDelete("/api/tenants/{tenantId}/role-assignments/{assignmentId}",
    (string tenantId, string assignmentId, AuthService auth, MembershipService members, RoleGrantService grants, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersMemberChangeRole) is { } denied) return denied;
    return grants.Revoke(tenantId, assignmentId) ? Results.NoContent() : Results.NotFound();
});

app.MapGet("/api/tenants/{tenantId}/custom-roles",
    (string tenantId, AuthService auth, MembershipService members, RoleGrantService grants, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersMemberView) is { } denied) return denied;
    return Results.Json(grants.CustomRolesFor(tenantId));
});

app.MapPost("/api/tenants/{tenantId}/custom-roles",
    (string tenantId, CreateCustomRoleRequest body, AuthService auth, MembershipService members, RoleGrantService grants, BillingService billing, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.MembersMemberChangeRole) is { } denied) return denied;
    // Defining custom roles is a paid entitlement (listing/granting them is not).
    if (!billing.HasFeature(tenantId, PlanFeatures.CustomRoles))
    {
        return Results.Json(
            new { error = "defining custom roles requires a paid plan", feature = PlanFeatures.CustomRoles },
            statusCode: StatusCodes.Status402PaymentRequired);
    }
    var creatorUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    return CustomRoleOutcomeResult(tenantId, grants.CreateCustomRole(
        tenantId, creatorUserId, ActorRole(req, auth, members, tenantId),
        body.RoleKey, body.Name, body.PermissionKeys ?? []));
});

// --- P3: two-party approvals (dynamic SoD, ADR 0020 §5) ---------------------
// An approval-gated permission (RequiresApproval) cannot be done in one shot: a
// requester who is entitled to the action opens a request, and a DISTINCT entitled
// party approves it, at which point the action runs. Entitlement is the normal gate
// with approval treated satisfied (so the two-party rule is the only thing left);
// the distinct-party rule itself is enforced by ApprovalService.
IResult DecideResult(ApprovalService.DecideOutcome outcome, Func<IResult> onOk) => outcome switch
{
    ApprovalService.DecideOutcome.Ok => onOk(),
    ApprovalService.DecideOutcome.NotFound => Results.NotFound(),
    ApprovalService.DecideOutcome.NotPending => Results.Conflict(new { error = "request is not pending" }),
    ApprovalService.DecideOutcome.SameParty => Results.Json(
        new { error = "the approver must be a different person than the requester" },
        statusCode: StatusCodes.Status403Forbidden),
    _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
};

// Perform a now-approved action. Extended as more actions become approval-gated.
IResult PerformApproved(ApprovalRequestEntity request, IControlPlaneStore store)
{
    if (request.PermissionKey == Permissions.TenantDeactivate.Key)
    {
        store.DeactivateTenant(request.TenantId);
    }
    return Results.NoContent();
}

app.MapPost("/api/tenants/{tenantId}/approvals",
    (string tenantId, CreateApprovalRequest body, ApprovalService approvals, AuthService auth, MembershipService members, HttpRequest req) =>
{
    var permission = body.PermissionKey is null ? null : Permissions.Find(body.PermissionKey);
    if (permission is null || !permission.RequiresApproval)
    {
        return Results.BadRequest(new { error = "unknown or non-approval-gated permission" });
    }
    // Entitlement to request = entitlement to the action itself, setting aside the
    // second-party requirement (approvalSatisfied). Tenant-wide actions gate at root.
    if (Forbidden(req, auth, members, tenantId, permission, approvalSatisfied: true) is { } denied) return denied;
    var requesterUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    var created = approvals.Create(tenantId, permission.Key, null, requesterUserId, body.TargetId, body.Note);
    return created is null
        ? Results.BadRequest(new { error = "could not create request" })
        : Results.Json(created, statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/api/tenants/{tenantId}/approvals",
    (string tenantId, ApprovalService approvals, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.TenantSettingsView) is { } denied) return denied;
    return Results.Json(approvals.Pending(tenantId));
});

app.MapPost("/api/tenants/{tenantId}/approvals/{requestId}/approve",
    (string tenantId, string requestId, ApprovalService approvals, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    var request = approvals.Find(tenantId, requestId);
    if (request is null) return Results.NotFound();
    var permission = Permissions.Find(request.PermissionKey);
    if (permission is null) return Results.NotFound();
    // The approver must themselves be entitled to the action (at the request's scope).
    if (Forbidden(req, auth, members, tenantId, permission, request.ScopeId, approvalSatisfied: true) is { } denied) return denied;
    var approverUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    return DecideResult(approvals.Approve(tenantId, requestId, approverUserId),
        () => PerformApproved(request, store));
});

app.MapPost("/api/tenants/{tenantId}/approvals/{requestId}/reject",
    (string tenantId, string requestId, ApprovalService approvals, AuthService auth, MembershipService members, HttpRequest req) =>
{
    var request = approvals.Find(tenantId, requestId);
    if (request is null) return Results.NotFound();
    var permission = Permissions.Find(request.PermissionKey);
    if (permission is null) return Results.NotFound();
    if (Forbidden(req, auth, members, tenantId, permission, request.ScopeId, approvalSatisfied: true) is { } denied) return denied;
    var approverUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    return DecideResult(approvals.Reject(tenantId, requestId, approverUserId), Results.NoContent);
});

// --- P6: platform super-admin ----------------------------------------------
// Gated by PLATFORM roles (not tenant membership). Grant/revoke platform roles is
// Root-Owner-only (platform.role.manage); the god-mode token bootstraps the first
// Root Owner, then Root manages the rest.
// Capability endpoint (§8): the caller's OWN platform roles, so the console can
// decide whether to show the platform surface. Authenticated users only; returns an
// empty list for a non-platform user (200, not 401 — the client checks the array).
app.MapGet("/api/platform/whoami",
    (AuthService auth, HttpRequest req) =>
{
    if (IsAdmin(req))
    {
        return Results.Json(new { roles = PlatformRoles.All.ToArray() });
    }
    var current = CurrentUser(req, auth);
    if (current is null)
    {
        return Results.Unauthorized();
    }
    return Results.Json(new { roles = platformAdmin.RolesFor(current.Value.User.Id).ToArray() });
});

app.MapGet("/api/platform/tenants",
    (IControlPlaneStore store, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantRead) is { } denied) return denied;
    return Results.Json(store.Tenants());
});

app.MapGet("/api/platform/role-assignments",
    (string? userId, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.RoleManage) is { } denied) return denied;
    return Results.Json(platformAdmin.Assignments(userId));
});

app.MapPost("/api/platform/role-assignments",
    (GrantPlatformRoleRequest body, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.RoleManage) is { } denied) return denied;
    var granterUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    var result = platformAdmin.Grant(granterUserId, body.UserId, body.Role, body.ExpiresAt, body.Reason);
    if (result.Outcome != PlatformAdminService.GrantOutcome.Ok)
    {
        return Results.BadRequest(new { error = "unknown platform role" });
    }
    platformAudit.Record("platform.role.granted", granterUserId, $"{body.Role} -> {body.UserId}");
    return Results.Created($"/api/platform/role-assignments/{result.Assignment!.Id}", result.Assignment);
});

app.MapDelete("/api/platform/role-assignments/{assignmentId}",
    (string assignmentId, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.RoleManage) is { } denied) return denied;
    return platformAdmin.Revoke(assignmentId) ? Results.NoContent() : Results.NotFound();
});

// Create an operator account under a named platform role, replacing the god-mode
// token's POST /api/admin/users. Root-Owner-only and MFA + fresh-auth gated: the
// caller chooses the initial password, so this is effectively the power to
// authenticate as the new account. Tenant users are NOT created here — they arrive
// through the invitation flow, which binds tenant, role and recipient to a
// single-use token. Every creation is written to the platform security log.
app.MapPost("/api/platform/users",
    (SignupRequest body, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.UserCreate) is { } denied) return denied;
    if (!AuthService.LooksLikeEmail(body.Email))
    {
        return Results.BadRequest(new { error = "a valid email address is required" });
    }
    if (!AuthService.PasswordAcceptable(body.Password))
    {
        return Results.BadRequest(new { error = "password must be 12 to 256 characters" });
    }
    var user = auth.CreateUser(body.Email, body.Password);
    if (user is null)
    {
        return Results.Conflict(new { error = "email is already registered" });
    }
    var actorUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    platformAudit.Record("platform.user.created", actorUserId, user.Id);
    return Results.Created($"/api/platform/users/{user.Id}", user);
});

// Seat the FIRST owner of an ownerless tenant, replacing the god-mode token's
// POST /api/admin/memberships. Provisioning a tenant creates the row but cannot put a
// human in it, so without this a new tenant is unreachable — every tenant endpoint
// requires membership.
//
// Confined to genuinely ownerless tenants on purpose: this rescues a stranded tenant,
// it does not let a platform operator insert themselves into a working one. A tenant
// that already has an owner manages its own members through the tenant endpoints.
app.MapPost("/api/platform/tenants/{tenantId}/memberships",
    (string tenantId, SeedMembershipRequest body, IControlPlaneStore store,
     MembershipService members, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.MembershipSeed) is { } denied) return denied;
    if (!store.TenantExists(tenantId)) return Results.NotFound();
    if (members.HasActiveOwner(tenantId))
    {
        return Results.Conflict(new
        {
            error = "tenant already has an active owner; manage members through the tenant's own endpoints",
        });
    }
    var role = string.IsNullOrWhiteSpace(body.Role) ? Roles.Owner : body.Role;
    if (!members.Grant(body.UserId, tenantId, role))
    {
        return Results.BadRequest(new { error = "unknown user or role" });
    }
    var actorUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    platformAudit.Record("platform.membership.seeded", actorUserId, $"{tenantId}:{body.UserId}:{role}");
    return Results.NoContent();
});

// Platform overview dashboard (§13.1): tenant counts by lifecycle state + plan, and a
// payment-health signal. Any platform role may read it (TenantRead).
app.MapGet("/api/platform/overview",
    (IControlPlaneStore store, BillingService billing, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantRead) is { } denied) return denied;
    return Results.Json(PlatformOverviewBuilder.Build(store.Tenants(), billing.EntitlementsFor));
});

// Platform tenant lifecycle (Operations) — provision, suspend, reactivate.
app.MapPost("/api/platform/tenants",
    (PlatformProvisionTenantRequest body, IControlPlaneStore store, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantProvision) is { } denied) return denied;
    if (string.IsNullOrWhiteSpace(body.Name)) return Results.BadRequest(new { error = "a tenant name is required" });
    var tenant = store.CreateTenant(body.Name);
    return Results.Created($"/api/platform/tenants/{tenant.Id}", tenant);
});

app.MapPost("/api/platform/tenants/{tenantId}/suspend",
    (string tenantId, IControlPlaneStore store, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantSuspend) is { } denied) return denied;
    return store.DeactivateTenant(tenantId) ? Results.NoContent() : Results.NotFound();
});

app.MapPost("/api/platform/tenants/{tenantId}/reactivate",
    (string tenantId, IControlPlaneStore store, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantSuspend) is { } denied) return denied;
    return store.ReactivateTenant(tenantId) ? Results.NoContent() : Results.NotFound();
});

// Platform subscription management (Billing) — set a tenant's plan cross-tenant.
app.MapPost("/api/platform/tenants/{tenantId}/subscription",
    (string tenantId, SetSubscriptionRequest body, IControlPlaneStore store, BillingService billing, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.SubscriptionManage) is { } denied) return denied;
    if (!store.TenantExists(tenantId)) return Results.NotFound();
    if (body.PlanId is null || !Plans.IsKnown(body.PlanId)) return Results.BadRequest(new { error = "unknown plan" });
    var status = body.Status ?? SubscriptionStatus.Active;
    billing.UpsertSubscription(tenantId, body.PlanId, status,
        null, null, authzClock.GetUtcNow().AddDays(30), false);
    // Let billing drive the lifecycle: past-due → grace, healthy → recover (guarded no-op otherwise).
    if (BillingLifecycle.OperationFor(status) is { } op)
    {
        store.TransitionTenant(tenantId, op);
    }
    return Results.Json(billing.EntitlementsFor(tenantId));
});

// Platform support-access grants (Support requests; Security approves; dynamic SoD).
IResult SupportDecideResult(PlatformSupportService.DecideOutcome outcome) => outcome switch
{
    PlatformSupportService.DecideOutcome.Ok => Results.NoContent(),
    PlatformSupportService.DecideOutcome.NotFound => Results.NotFound(),
    PlatformSupportService.DecideOutcome.NotPending => Results.Conflict(new { error = "request is not pending" }),
    PlatformSupportService.DecideOutcome.SameParty => Results.Json(
        new { error = "the approver must be a different person than the requester" },
        statusCode: StatusCodes.Status403Forbidden),
    _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
};

app.MapPost("/api/platform/support-grants",
    (RequestSupportGrantRequest body, IControlPlaneStore store, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.SupportRequest) is { } denied) return denied;
    if (string.IsNullOrWhiteSpace(body.SubjectTenantId) || !store.TenantExists(body.SubjectTenantId))
    {
        return Results.BadRequest(new { error = "unknown tenant" });
    }
    var requesterUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    var grant = platformSupport.Request(
        body.SubjectTenantId, requesterUserId, body.Reason ?? "", body.DurationMinutes ?? 60);
    return Results.Json(grant, statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/api/platform/support-grants",
    (AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.SupportApprove) is { } denied) return denied;
    return Results.Json(platformSupport.Pending());
});

app.MapPost("/api/platform/support-grants/{grantId}/approve",
    (string grantId, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.SupportApprove) is { } denied) return denied;
    var approverUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    var outcome = platformSupport.Approve(grantId, approverUserId);
    if (outcome == PlatformSupportService.DecideOutcome.Ok)
    {
        platformAudit.Record("platform.support.approved", approverUserId, grantId);
    }
    return SupportDecideResult(outcome);
});

app.MapPost("/api/platform/support-grants/{grantId}/reject",
    (string grantId, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.SupportApprove) is { } denied) return denied;
    var deciderUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    return SupportDecideResult(platformSupport.Reject(grantId, deciderUserId));
});

// Platform tenant offboarding (two-party, §9) — a distinct approver executes the
// terminal offboarding.
app.MapPost("/api/platform/offboard-requests",
    (RequestOffboardRequest body, IControlPlaneStore store, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantOffboard) is { } denied) return denied;
    if (string.IsNullOrWhiteSpace(body.SubjectTenantId) || !store.TenantExists(body.SubjectTenantId))
    {
        return Results.BadRequest(new { error = "unknown tenant" });
    }
    var requesterUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    var request = platformOffboard.Request(body.SubjectTenantId, requesterUserId, body.Reason ?? "");
    return Results.Json(request, statusCode: StatusCodes.Status202Accepted);
});

app.MapGet("/api/platform/offboard-requests",
    (AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantOffboard) is { } denied) return denied;
    return Results.Json(platformOffboard.Pending());
});

app.MapPost("/api/platform/offboard-requests/{requestId}/approve",
    (string requestId, IControlPlaneStore store, BillingService billing, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantOffboard) is { } denied) return denied;
    var approverUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    var (outcome, subjectTenantId) = platformOffboard.Approve(requestId, approverUserId);
    if (outcome == PlatformOffboardService.DecideOutcome.Ok)
    {
        // Approval BEGINS the offboarding pipeline (active → offboarding): it is now
        // cancellable during cooling-off and completed by a separate archive step,
        // rather than jumping straight to the terminal archived state. The cooling-off
        // window is the tenant's plan retention entitlement (§12 retention_days).
        store.TransitionTenant(subjectTenantId!, TenantLifecycleOperation.BeginOffboarding,
            billing.RetentionWindowFor(subjectTenantId!));
        platformAudit.Record("platform.tenant.offboarding_started", approverUserId, subjectTenantId!);
        return Results.NoContent();
    }
    return outcome switch
    {
        PlatformOffboardService.DecideOutcome.NotFound => Results.NotFound(),
        PlatformOffboardService.DecideOutcome.NotPending => Results.Conflict(new { error = "request is not pending" }),
        PlatformOffboardService.DecideOutcome.SameParty => Results.Json(
            new { error = "the approver must be a different person than the requester" },
            statusCode: StatusCodes.Status403Forbidden),
        _ => Results.StatusCode(StatusCodes.Status500InternalServerError),
    };
});

app.MapPost("/api/platform/offboard-requests/{requestId}/reject",
    (string requestId, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantOffboard) is { } denied) return denied;
    var deciderUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    return platformOffboard.Reject(requestId, deciderUserId) switch
    {
        PlatformOffboardService.DecideOutcome.Ok => Results.NoContent(),
        PlatformOffboardService.DecideOutcome.NotFound => Results.NotFound(),
        _ => Results.Conflict(new { error = "request is not pending" }),
    };
});

// Complete an offboarding into the terminal archived state (after export/retention/
// legal-hold checks). Only legal from the offboarding state (the state machine guards it).
app.MapPost("/api/platform/tenants/{tenantId}/archive",
    (string tenantId, IControlPlaneStore store, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantOffboard) is { } denied) return denied;
    var actorUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    var outcome = store.TransitionTenant(tenantId, TenantLifecycleOperation.Archive);
    if (outcome == TenantTransitionOutcome.Ok)
    {
        platformAudit.Record("platform.tenant.archived", actorUserId, tenantId);
        return Results.NoContent();
    }
    return outcome switch
    {
        TenantTransitionOutcome.NotFound => Results.NotFound(),
        TenantTransitionOutcome.LegalHold => Results.Conflict(
            new { error = "a legal hold is in place; lift it before archiving" }),
        TenantTransitionOutcome.CoolingOff => Results.Conflict(
            new { error = "the cooling-off window has not yet elapsed" }),
        _ => Results.Conflict(new { error = "tenant is not in the offboarding state" }),
    };
});

// Place or lift a legal hold, which overrides archiving/deletion (§10.3).
app.MapPost("/api/platform/tenants/{tenantId}/legal-hold",
    (string tenantId, SetLegalHoldRequest body, IControlPlaneStore store, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantOffboard) is { } denied) return denied;
    var actorUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    if (!store.SetTenantLegalHold(tenantId, body.Hold))
    {
        return Results.NotFound();
    }
    platformAudit.Record(body.Hold ? "platform.tenant.legal_hold_placed" : "platform.tenant.legal_hold_lifted",
        actorUserId, tenantId);
    return Results.NoContent();
});

// Export a tenant's control-plane data as an artifact (§10.3 export step). Records the
// export in the platform audit trail (who exported which tenant, when).
app.MapGet("/api/platform/tenants/{tenantId}/export",
    (string tenantId, IControlPlaneStore store, MembershipService members, BillingService billing,
     AuthService auth, TimeProvider clock, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantExport) is { } denied) return denied;
    var export = TenantExporter.Build(store, clock.GetUtcNow(), tenantId,
        members.MembersOf(tenantId).ToList(),
        members.InvitationsFor(tenantId).ToList(),
        billing.SubscriptionFor(tenantId));
    if (export is null)
    {
        return Results.NotFound();
    }
    var actorUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    platformAudit.Record("platform.tenant.exported", actorUserId, tenantId);
    return Results.Json(export);
});

// Cancel offboarding during cooling-off, returning the tenant to active.
app.MapPost("/api/platform/tenants/{tenantId}/cancel-offboarding",
    (string tenantId, IControlPlaneStore store, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.TenantOffboard) is { } denied) return denied;
    var actorUserId = CurrentUser(req, auth)?.User.Id ?? "platform-admin";
    var outcome = store.TransitionTenant(tenantId, TenantLifecycleOperation.CancelOffboarding);
    if (outcome == TenantTransitionOutcome.Ok)
    {
        platformAudit.Record("platform.tenant.offboarding_cancelled", actorUserId, tenantId);
        return Results.NoContent();
    }
    return outcome == TenantTransitionOutcome.NotFound
        ? Results.NotFound()
        : Results.Conflict(new { error = "tenant is not in the offboarding state" });
});

// Platform security/audit event log (Security + Auditor review).
app.MapGet("/api/platform/security-events",
    (int? limit, AuthService auth, HttpRequest req) =>
{
    if (PlatformForbidden(req, auth, PlatformPermissions.SecurityEventRead) is { } denied) return denied;
    return Results.Json(platformAudit.Recent(limit ?? 100));
});

// --- identity: users + sessions (Phase C1) ---------------------------------
// Session resolution: `Authorization: Bearer ses_…` (SPA) or the `lc_session`
// HttpOnly cookie (same-site browser use). Cookie hardening to __Host- prefix +
// CSRF double-submit lands when the web app is served same-origin (Phase H).
(UserView User, string SessionId, bool MfaSatisfied, bool FreshAuth)? CurrentUser(HttpRequest req, AuthService auth)
{
    var header = req.Headers.Authorization.ToString();
    string? token = null;
    if (header.StartsWith("Bearer ses_", StringComparison.Ordinal))
    {
        token = header["Bearer ".Length..];
    }
    else if (req.Cookies.TryGetValue("lc_session", out var cookie))
    {
        token = cookie;
    }
    return token is null ? null : auth.Authenticate(token);
}

// Tenant-scoped authorization: the platform admin token passes everything;
// otherwise the session user's roles at the tenant ROOT scope must satisfy the
// permission. Checked server-side on every tenant operation (no client claims).
// Scope-aware gate (ADR 0020, over the ADR 0019 engine). Returns null when the
// request is permitted, or the IResult to return when it is denied.
//
// Status choice preserves anti-enumeration: an unauthenticated caller OR a
// non-member gets 401 — indistinguishable from "no such tenant", so it never
// reveals a tenant's existence or a user's (lack of) membership across tenants
// (the cross-tenant IDOR case). A caller who IS a member but whose roles lack the
// permission (or who needs step-up) gets 403 with the engine's reason — safe,
// since they already know it is their tenant, and the reason drives the UI (e.g.
// prompting re-authentication for a fresh-auth-gated action).
IResult? Forbidden(HttpRequest req, AuthService auth, MembershipService members,
    string tenantId, PermissionDefinition permission, string? resourceScopeId = null,
    bool approvalSatisfied = false)
{
    // BREAK-GLASS. The god-mode token used to `return null` here, which skipped the
    // engine entirely — and with it every RequiresMfa / RequiresFreshAuth /
    // RequiresApproval flag. Concretely, Permissions.TenantDeactivate declares
    // `requiresApproval: true` ("a distinct second party must approve"), yet the token
    // could deactivate any tenant unilaterally in a single call.
    //
    // It now runs THROUGH the engine as Owner. Owner holds all five legacy
    // capabilities, so every permission the token legitimately reached still resolves —
    // but the governance flags now apply. MFA and fresh-auth are treated as satisfied
    // (the token is an out-of-band pre-shared secret, and the bootstrap flows it exists
    // for are themselves MFA-gated), while approval is NOT: two-party approval is a
    // property of the tenant's own governance and no credential should be able to
    // stand in for the second party.
    var breakGlass = IsAdmin(req);
    string userId;
    string? role;
    (UserView User, string SessionId, bool MfaSatisfied, bool FreshAuth)? current = null;
    var viaSupportGrant = false;

    if (breakGlass)
    {
        userId = PlatformAdminActor;
        role = Roles.Owner;
        BreakGlassLog.TenantAccessedWithAdminToken(app.Logger, tenantId, permission.Key);
        // Also record it durably. Logs are a small rolling buffer with no guaranteed
        // retention, so "no break-glass in the log tail" cannot answer "has the token
        // been used this month?" — which is exactly the question that gates retiring
        // it. platform_security_events is append-only and timestamped, so the readiness
        // check becomes a precise query over a real window instead of a guess.
        platformAudit.Record("platform.break_glass.used", PlatformAdminActor,
            $"{tenantId}:{permission.Key}");
    }
    else
    {
        current = CurrentUser(req, auth);
        if (current is null)
        {
            return Results.Unauthorized();
        }
        userId = current.Value.User.Id;
        role = members.RoleIn(userId, tenantId);
    }

    if (role is null)
    {
        // Support access (prompt §13.3): a platform support engineer holding an
        // APPROVED, UNEXPIRED, tenant-scoped grant may act in a tenant they are not a
        // member of. This is the sanctioned replacement for impersonation — and for
        // the god-mode token's blanket cross-tenant reach.
        //
        // It confers `read-only` and nothing more: diagnosis, never mutation. A
        // support engineer must not be able to decommission a gateway, change a role
        // or deactivate a tenant on the strength of a support ticket. Anything
        // destructive still requires real membership.
        //
        // Deliberately checked only AFTER membership: a genuine member keeps their own
        // (possibly higher) role rather than being narrowed by holding a grant.
        if (!platformSupport.HasActiveGrant(tenantId, userId))
        {
            return Results.Unauthorized(); // non-member: indistinguishable from no access
        }
        role = Roles.ReadOnly;
        viaSupportGrant = true;
        SupportAccessLog.TenantAccessedViaSupportGrant(app.Logger, userId, tenantId, permission.Key);
    }

    var (tree, rootId, assignments, customGrants) = RootScopeContext(tenantId, userId, role);
    // Authorize at the resource's own scope when it names one that exists in the
    // tree; otherwise at the tenant root (tenant-wide endpoints, or an unscoped/
    // unknown resource). The membership role sits at the root and so still reaches
    // any target — this only ever narrows which explicit grants apply.
    var targetScopeId = resourceScopeId is not null && tree.Find(resourceScopeId) is not null
        ? resourceScopeId
        : rootId;
    var decision = scopedEngine.Authorize(new ScopedAuthorizationRequest(
        assignments, tree, userId, targetScopeId, permission.Key,
        authzClock.GetUtcNow(),
        // Break-glass satisfies the assurance gates (out-of-band secret) but NOT the
        // two-party gate below.
        MfaSatisfied: breakGlass || current!.Value.MfaSatisfied,
        FreshAuth: breakGlass || current!.Value.FreshAuth,
        // A support-grant caller can never satisfy a two-party approval: they are not a
        // member of this tenant, so they must not count as the second party in its own
        // approval flow. (read-only cannot reach an approval-gated permission anyway —
        // this is the belt to that braces.)
        //
        // Break-glass does not auto-satisfy it either: approval is the tenant's own
        // governance, and a credential must not substitute for the second party.
        ApprovalGranted: approvalSatisfied && !viaSupportGrant,
        CustomGrants: customGrants));
    return decision.IsAllowed
        ? null
        : Results.Json(
            new { error = decision.Reason, stepUp = decision.RequiresStepUp },
            statusCode: StatusCodes.Status403Forbidden);
}

// The scope context for a tenant-wide endpoint: authorize at the tenant ROOT with
// the membership role synthesized as a root assignment (so tenant-wide access is
// preserved exactly), unioned with the caller's persisted root-level grants. A
// tenant with no persisted scopes yet uses a synthetic single-root tree, so the
// gate never depends on the startup backfill having run.
(ScopeTree Tree, string RootId, IReadOnlyCollection<RoleAssignment> Assignments,
    IReadOnlyDictionary<string, IReadOnlySet<string>>? CustomGrants)
    RootScopeContext(string tenantId, string userId, string membershipRole)
{
    var tree = scopeService.Tree(tenantId);
    if (tree is null)
    {
        // No persisted scopes ⇒ no scoped/custom grants possible; membership only.
        var synthetic = ScopeTree.Build([new ScopeNode($"root:{tenantId}", tenantId, ScopeType.Tenant, "", null)]);
        return (synthetic, synthetic.Root.Id,
            [new RoleAssignment("membership", userId, membershipRole, synthetic.Root.Id, null)], null);
    }
    var assignments = roleGrants.ActiveAssignmentsFor(tenantId, userId).ToList();
    assignments.Add(new RoleAssignment("membership", userId, membershipRole, tree.Root.Id, null));
    // Only pay for the custom-grant lookup when the caller actually holds a
    // non-baseline role somewhere.
    var customGrants = assignments.Any(a => !Roles.All.Contains(a.Role))
        ? roleGrants.CustomGrantsFor(tenantId)
        : null;
    return (tree, tree.Root.Id, assignments, customGrants);
}

// Platform (super-admin) authorization gate (P6, program prompt §8). Disjoint from
// the tenant Forbidden: it consults the caller's PLATFORM role assignments, never
// tenant membership. The god-mode admin token bypasses (bootstrap/break-glass) — it
// is retired to break-glass-only in a later slice. Anti-enumeration: a non-platform
// user gets 401 (indistinguishable from "no such surface"); a platform user whose
// roles lack the permission (or who needs step-up) gets 403 + reason.
IResult? PlatformForbidden(HttpRequest req, AuthService auth, PlatformPermissionDefinition permission)
{
    if (IsAdmin(req))
    {
        return null;
    }
    var current = CurrentUser(req, auth);
    if (current is null)
    {
        return Results.Unauthorized();
    }
    var roles = platformAdmin.RolesFor(current.Value.User.Id);
    if (roles.Count == 0)
    {
        return Results.Unauthorized(); // not a platform user
    }
    var decision = platformEngine.Authorize(new PlatformAuthorizationRequest(
        roles.ToList(), permission.Key,
        MfaSatisfied: current.Value.MfaSatisfied,
        FreshAuth: current.Value.FreshAuth));
    return decision.IsAllowed
        ? null
        : Results.Json(
            new { error = decision.Reason, stepUp = decision.RequiresStepUp },
            statusCode: StatusCodes.Status403Forbidden);
}

void SetSessionCookie(HttpResponse res, string token, DateTimeOffset expires) =>
    res.Cookies.Append("lc_session", token, new CookieOptions
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Expires = expires,
        Path = "/",
    });

// Self-service signup is a business-policy gate, disabled unless configured.
app.MapPost("/api/auth/signup", (SignupRequest body, AuthService auth) =>
{
    if (!string.Equals(app.Configuration["ControlPlane:AllowSignup"], "true", StringComparison.OrdinalIgnoreCase))
    {
        return Results.NotFound();
    }
    if (!AuthService.LooksLikeEmail(body.Email))
    {
        return Results.BadRequest(new { error = "a valid email address is required" });
    }
    if (!AuthService.PasswordAcceptable(body.Password))
    {
        return Results.BadRequest(new { error = "password must be 12 to 256 characters" });
    }
    var user = auth.CreateUser(body.Email, body.Password);
    // Generic response either way: no account-existence oracle.
    return user is null ? Results.Ok(new { status = "ok" }) : Results.Ok(new { status = "ok" });
}).RequireRateLimiting("login");

// Platform admin creates users while self-service signup is disabled.
app.MapPost("/api/admin/users", (SignupRequest body, AuthService auth, HttpRequest req) =>
{
    if (!IsAdmin(req)) return Results.Unauthorized();
    if (!AuthService.LooksLikeEmail(body.Email))
    {
        return Results.BadRequest(new { error = "a valid email address is required" });
    }
    if (!AuthService.PasswordAcceptable(body.Password))
    {
        return Results.BadRequest(new { error = "password must be 12 to 256 characters" });
    }
    var user = auth.CreateUser(body.Email, body.Password);
    return user is null
        ? Results.Conflict(new { error = "email is already registered" })
        : Results.Created($"/api/admin/users/{user.Id}", user);
});

app.MapPost("/api/auth/login", (LoginRequest body, AuthService auth, HttpResponse res) =>
{
    var outcome = auth.Login(body.Email, body.Password);
    if (outcome is null)
    {
        return Results.Unauthorized();
    }
    if (outcome.MfaRequired)
    {
        return Results.Json(new { mfaRequired = true, mfaToken = outcome.MfaToken });
    }
    SetSessionCookie(res, outcome.Session!.SessionToken, outcome.Session.ExpiresAt);
    return Results.Json(outcome.Session);
}).RequireRateLimiting("login");

// --- MFA: enrollment + challenge completion (Phase C4) ----------------------
app.MapPost("/api/auth/mfa/setup", (AuthService auth, HttpRequest req) =>
{
    var current = CurrentUser(req, auth);
    if (current is null) return Results.Unauthorized();
    var setup = auth.SetupMfa(current.Value.User.Id);
    return setup is null
        ? Results.BadRequest(new { error = "MFA is already enabled" })
        : Results.Json(setup);
});

app.MapPost("/api/auth/mfa/enable", (MfaCodeRequest body, AuthService auth, HttpRequest req) =>
{
    var current = CurrentUser(req, auth);
    if (current is null) return Results.Unauthorized();
    var codes = auth.EnableMfa(current.Value.User.Id, body.Code);
    return codes is null
        ? Results.BadRequest(new { error = "run setup first and enter a current code" })
        : Results.Json(new { recoveryCodes = codes });
});

app.MapPost("/api/auth/mfa/disable", (MfaCodeRequest body, AuthService auth, HttpRequest req) =>
{
    var current = CurrentUser(req, auth);
    if (current is null) return Results.Unauthorized();
    return auth.DisableMfa(current.Value.User.Id, body.Code)
        ? Results.NoContent()
        : Results.BadRequest(new { error = "a current code is required to disable MFA" });
});

app.MapPost("/api/auth/mfa/verify", (MfaVerifyRequest body, AuthService auth, HttpResponse res) =>
{
    var result = auth.VerifyMfaLogin(body.MfaToken, body.Code);
    if (result is null) return Results.Unauthorized();
    SetSessionCookie(res, result.SessionToken, result.ExpiresAt);
    return Results.Json(result);
}).RequireRateLimiting("login");

app.MapPost("/api/auth/mfa/recover", (MfaRecoverRequest body, AuthService auth, HttpResponse res) =>
{
    var result = auth.RecoverMfaLogin(body.MfaToken, body.RecoveryCode);
    if (result is null) return Results.Unauthorized();
    SetSessionCookie(res, result.SessionToken, result.ExpiresAt);
    return Results.Json(result);
}).RequireRateLimiting("login");

app.MapGet("/api/auth/me", (AuthService auth, HttpRequest req) =>
{
    var current = CurrentUser(req, auth);
    return current is null ? Results.Unauthorized() : Results.Json(current.Value.User);
});

// Step-up: re-verify credentials to refresh this session's fresh-auth window for
// high-risk (RequiresFreshAuth) permissions. Requires the password, plus a current
// MFA code when the account has MFA enabled. Rate-limited like login.
app.MapPost("/api/auth/step-up", (StepUpRequest body, AuthService auth, HttpRequest req) =>
{
    var current = CurrentUser(req, auth);
    if (current is null)
    {
        return Results.Unauthorized();
    }
    return auth.StepUp(current.Value.SessionId, current.Value.User.Id, body.Password ?? string.Empty, body.Code)
        ? Results.NoContent()
        : Results.Unauthorized();
}).RequireRateLimiting("login");

app.MapPost("/api/auth/logout", (AuthService auth, HttpRequest req, HttpResponse res) =>
{
    var current = CurrentUser(req, auth);
    if (current is null)
    {
        return Results.Unauthorized();
    }
    auth.RevokeSession(current.Value.User.Id, current.Value.SessionId);
    res.Cookies.Delete("lc_session");
    return Results.NoContent();
});

app.MapGet("/api/auth/sessions", (AuthService auth, HttpRequest req) =>
{
    var current = CurrentUser(req, auth);
    return current is null
        ? Results.Unauthorized()
        : Results.Json(auth.SessionsFor(current.Value.User.Id, current.Value.SessionId));
});

app.MapPost("/api/auth/sessions/revoke-all", (AuthService auth, HttpRequest req, HttpResponse res) =>
{
    var current = CurrentUser(req, auth);
    if (current is null)
    {
        return Results.Unauthorized();
    }
    var count = auth.RevokeAllSessions(current.Value.User.Id);
    res.Cookies.Delete("lc_session");
    return Results.Json(new { revoked = count });
});

// --- identity: email verification + password reset (Phase C2) --------------
// Links use ControlPlane:PublicBaseUrl (the web console origin at launch).
string Link(string path, string token) =>
    $"{(app.Configuration["ControlPlane:PublicBaseUrl"] ?? "http://localhost:5173").TrimEnd('/')}{path}?token={token}";

app.MapPost("/api/auth/send-verification", async (AuthService auth, IEmailSender mail, HttpRequest req) =>
{
    var current = CurrentUser(req, auth);
    if (current is null)
    {
        return Results.Unauthorized();
    }
    var issued = auth.IssueVerification(current.Value.User.Id);
    if (issued is not null)
    {
        await mail.SendAsync(EmailTemplates.VerifyEmail(issued.Value.Email, Link("/verify-email", issued.Value.Token)));
    }
    return Results.Accepted();
}).RequireRateLimiting("login");

app.MapPost("/api/auth/verify-email", (TokenRequest body, AuthService auth) =>
    auth.VerifyEmail(body.Token)
        ? Results.NoContent()
        : Results.BadRequest(new { error = "invalid or expired link; request a new one" }));

// Always 202 regardless of account existence (no oracle); rate limited.
app.MapPost("/api/auth/forgot-password", async (ForgotPasswordRequest body, AuthService auth, IEmailSender mail) =>
{
    var issued = auth.IssuePasswordReset(body.Email);
    if (issued is not null)
    {
        // Best-effort: a send failure must not turn into a 500 here, or an
        // existing account would be distinguishable from an unknown one.
        await MailDelivery.TrySendAsync(mail,
            EmailTemplates.ResetPassword(issued.Value.Email, Link("/reset-password", issued.Value.Token)),
            "password-reset", app.Logger);
    }
    return Results.Accepted();
}).RequireRateLimiting("login");

app.MapPost("/api/auth/reset-password", (ResetPasswordRequest body, AuthService auth) =>
{
    if (!AuthService.PasswordAcceptable(body.NewPassword))
    {
        return Results.BadRequest(new { error = "password must be 12 to 256 characters" });
    }
    return auth.ResetPassword(body.Token, body.NewPassword)
        ? Results.NoContent()
        : Results.BadRequest(new { error = "invalid or expired link; request a new one" });
}).RequireRateLimiting("login");

// --- billing: plans + entitlements (Phase E1) ------------------------------
// The plan catalog is public (no prices — entitlement scope only).
app.MapGet("/api/billing/plans", () => Results.Json(Plans.All.Select(p => new
{
    id = p.Id,
    name = p.Name,
    gatewayQuota = p.GatewayQuota,
    features = p.Features,
})));

// A tenant's current subscription + entitlements. Any member may read the plan
// and entitlements (the gateway quota affects everyone); provider ids never
// appear in this payload.
app.MapGet("/api/tenants/{tenantId}/billing", (string tenantId, BillingService billing, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.BillingSubscriptionView) is { } denied) return denied;
    return Results.Json(new
    {
        entitlements = billing.EntitlementsFor(tenantId),
        subscription = billing.SubscriptionFor(tenantId),
    });
});

// Start hosted checkout for a plan. Only a billing manager may spend money; the
// provider owns the payment page (no card data ever reaches this API).
app.MapPost("/api/tenants/{tenantId}/billing/checkout",
    async (string tenantId, CheckoutRequest body, IBillingProvider provider, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.BillingSubscriptionManage) is { } denied) return denied;
    if (body.PlanId is null || !Plans.IsKnown(body.PlanId) || body.PlanId == Plans.Trial)
    {
        return Results.BadRequest(new { error = "unknown or non-purchasable plan" });
    }
    var redirect = await provider.CreateCheckoutAsync(tenantId, body.PlanId);
    return Results.Json(new { url = redirect.Url });
});

// Open the provider's billing portal (update card, cancel, view invoices).
app.MapPost("/api/tenants/{tenantId}/billing/portal",
    async (string tenantId, IBillingProvider provider, BillingService billing, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (Forbidden(req, auth, members, tenantId, Permissions.BillingPortalOpen) is { } denied) return denied;
    var customerId = billing.ProviderCustomerIdFor(tenantId);
    var redirect = await provider.CreatePortalAsync(tenantId, customerId);
    return Results.Json(new { url = redirect.Url });
});

// Provider webhook: the only unauthenticated write here, gated entirely by the
// provider's signature verification. Applied exactly once (idempotent + replay-
// safe via the billing_events unique index). Always 200 on a valid signature so
// the provider does not retry a duplicate we intentionally ignored.
app.MapPost("/api/billing/webhook", async (IBillingProvider provider, BillingService billing, IControlPlaneStore store, HttpRequest req) =>
{
    using var reader = new StreamReader(req.Body);
    var payload = await reader.ReadToEndAsync();
    var signature = req.Headers.TryGetValue(provider.SignatureHeaderName, out var sig) ? sig.ToString() : null;

    var ev = provider.ParseWebhook(payload, signature);
    if (ev is null)
    {
        // Bad signature or unparseable payload — reject without revealing which.
        return Results.StatusCode(StatusCodes.Status400BadRequest);
    }
    var applied = billing.TryApplyProviderEvent(ev);
    // Only drive the lifecycle when this call actually applied the event (not a replay),
    // so a duplicate delivery can't re-trigger a grace/recover transition.
    if (applied && BillingLifecycle.OperationFor(ev.Status) is { } op)
    {
        store.TransitionTenant(ev.TenantId, op);
    }
    return Results.Json(new { applied });
});

// --- gateway enrollment (bootstrap token is the credential) ----------------
app.MapPost("/api/gateways/enroll", (EnrollRequest body, IControlPlaneStore store) =>
{
    var result = store.Enroll(body.BootstrapToken, string.IsNullOrWhiteSpace(body.Name) ? "gateway" : body.Name.Trim());
    return result is null ? Results.Unauthorized() : Results.Json(result);
});

// A gateway reports liveness, authenticated by its device credential. A
// decommissioned gateway has no credential and is rejected.
app.MapPost("/api/gateways/heartbeat", (IControlPlaneStore store, HttpRequest req) =>
{
    var gatewayId = req.Headers["X-Gateway-Id"].ToString();
    var credential = req.Headers["X-Gateway-Credential"].ToString();
    var tenantId = string.IsNullOrEmpty(gatewayId) ? null : store.ValidateDeviceCredential(gatewayId, credential);
    if (tenantId is null)
    {
        return Results.Unauthorized();
    }
    store.RecordHeartbeat(tenantId, gatewayId);
    return Results.NoContent();
});

// A gateway reports PHI-free operational telemetry (message counts + last capture
// time), authenticated by its device credential. This also counts as a heartbeat.
// The payload carries no message content or result values — only counts.
app.MapPost("/api/gateways/telemetry", (GatewayTelemetryRequest body, IControlPlaneStore store, HttpRequest req) =>
{
    var gatewayId = req.Headers["X-Gateway-Id"].ToString();
    var credential = req.Headers["X-Gateway-Credential"].ToString();
    var tenantId = string.IsNullOrEmpty(gatewayId) ? null : store.ValidateDeviceCredential(gatewayId, credential);
    if (tenantId is null)
    {
        return Results.Unauthorized();
    }
    // Clamp to non-negative; the edge reports counts, never negatives.
    var telemetry = new GatewayTelemetry(
        Math.Max(0, body.Captured), Math.Max(0, body.Pending),
        Math.Max(0, body.Delivered), Math.Max(0, body.Dead), body.LastCaptureAt);
    return store.RecordTelemetry(tenantId, gatewayId, telemetry) ? Results.NoContent() : Results.NotFound();
});

// A gateway fetches its own config, authenticated by its device credential.
app.MapGet("/api/gateways/config", (IControlPlaneStore store, HttpRequest req) =>
{
    var gatewayId = req.Headers["X-Gateway-Id"].ToString();
    var credential = req.Headers["X-Gateway-Credential"].ToString();
    var tenantId = string.IsNullOrEmpty(gatewayId) ? null : store.ValidateDeviceCredential(gatewayId, credential);
    if (tenantId is null)
    {
        return Results.Unauthorized();
    }
    // An authenticated config fetch is also a liveness signal.
    store.RecordHeartbeat(tenantId, gatewayId);
    var config = store.CurrentConfig(tenantId, gatewayId);
    return config is null ? Results.NoContent() : Results.Json(config);
});

// SPA client-side routing: any request not matched above and not a real static
// file is served index.html so the browser router can handle it. Unknown /api/*
// paths keep returning 404 (JSON callers expect that, not an HTML page) via the
// more specific fallback, which wins over the catch-all.
app.MapFallback("/api/{**rest}", () => Results.NotFound());
app.MapFallbackToFile("index.html");

app.Run();

/// <summary>Minimal, PHI-free health payload.</summary>
/// <summary>Health payload. <paramref name="Database"/> names the active provider so a
/// silent in-memory fallback (missing DATABASE_URL) is visible rather than reported
/// green; null on the liveness endpoint, which checks no dependencies.</summary>
internal sealed record HealthResponse(string Status, string Service, string Version, string? Database = null);

/// <summary>Request to create a tenant.</summary>
internal sealed record CreateTenantRequest(string Name);

/// <summary>Request to enroll a gateway using a bootstrap token.</summary>
internal sealed record EnrollRequest(string BootstrapToken, string? Name);

/// <summary>Request to create a user account.</summary>
internal sealed record SignupRequest(string Email, string Password);

/// <summary>Login request.</summary>
internal sealed record LoginRequest(string Email, string Password);

/// <summary>A single-use account token presented back to the API.</summary>
internal sealed record TokenRequest(string Token);

/// <summary>Password-reset request (response never reveals account existence).</summary>
internal sealed record ForgotPasswordRequest(string Email);

/// <summary>Completes a password reset.</summary>
internal sealed record ResetPasswordRequest(string Token, string NewPassword);

internal sealed record StepUpRequest(string? Password, string? Code);

/// <summary>Platform-admin membership grant (tenant bootstrap).</summary>
internal sealed record GrantMembershipRequest(string UserId, string TenantId, string Role);

/// <summary>Invite a user into a tenant with a role.</summary>
internal sealed record InviteRequest(string Email, string Role);

/// <summary>Change an existing member's role.</summary>
internal sealed record ChangeRoleRequest(string Role);

/// <summary>Create a scope: a tenant root (no parent) or a child of an existing scope.</summary>
internal sealed record CreateScopeRequest(string? ParentId, string? Type, string? Name);

/// <summary>Pin a gateway to an org scope (null clears it to tenant-wide).</summary>
internal sealed record AssignScopeRequest(string? ScopeId);

/// <summary>Open a two-party approval request for an approval-gated permission.</summary>
internal sealed record CreateApprovalRequest(string? PermissionKey, string? TargetId, string? Note);

/// <summary>Assign a platform (super-admin) role to a user (P6).</summary>
internal sealed record GrantPlatformRoleRequest(string UserId, string Role, DateTimeOffset? ExpiresAt, string? Reason);

/// <summary>Provision a new tenant from the platform surface (P6).</summary>
internal sealed record PlatformProvisionTenantRequest(string Name);

/// <summary>Set a tenant's subscription plan from the platform surface (P6).</summary>
internal sealed record SetSubscriptionRequest(string? PlanId, string? Status);

/// <summary>Request time-limited support access to a tenant (P6).</summary>
internal sealed record RequestSupportGrantRequest(string SubjectTenantId, string? Reason, int? DurationMinutes);

/// <summary>Request the terminal offboarding of a tenant (P6, two-party).</summary>
internal sealed record RequestOffboardRequest(string SubjectTenantId, string? Reason);

/// <summary>Place or lift a legal hold on a tenant (P7, §10.3).</summary>
internal sealed record SetLegalHoldRequest(bool Hold);

/// <summary>Seat the first owner of an ownerless tenant. Role defaults to owner.</summary>
internal sealed record SeedMembershipRequest(string UserId, string? Role);

/// <summary>Grant a role to a user at a scope (P3 scoped assignment).</summary>
internal sealed record GrantRoleRequest(string UserId, string Role, string ScopeId, DateTimeOffset? ExpiresAt);

/// <summary>Define a tenant custom role from a set of permission keys (P3).</summary>
internal sealed record CreateCustomRoleRequest(string RoleKey, string Name, IReadOnlyList<string>? PermissionKeys);

/// <summary>A created invitation plus whether the provider accepted its email.</summary>
internal sealed record InvitationCreatedResponse(InvitationView Invitation, bool EmailDelivered);

/// <summary>Rename a tenant.</summary>
internal sealed record RenameTenantRequest(string? Name);

/// <summary>Begin checkout for a plan.</summary>
internal sealed record CheckoutRequest(string? PlanId);

/// <summary>A gateway's PHI-free telemetry self-report (counts + last capture).</summary>
internal sealed record GatewayTelemetryRequest(
    long Captured, long Pending, long Delivered, long Dead, DateTimeOffset? LastCaptureAt);

/// <summary>A TOTP code for enabling/disabling MFA.</summary>
internal sealed record MfaCodeRequest(string Code);

/// <summary>Completes an MFA login challenge with a TOTP code.</summary>
internal sealed record MfaVerifyRequest(string MfaToken, string Code);

/// <summary>Completes an MFA login challenge with a recovery code.</summary>
internal sealed record MfaRecoverRequest(string MfaToken, string RecoveryCode);

// Exposed so integration tests can host the app via WebApplicationFactory.
public partial class Program;
