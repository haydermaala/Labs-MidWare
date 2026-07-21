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
// Central authorization engine (P2). Registered now; endpoints move onto it in a
// shadow phase before it becomes the sole gate (see ADR 0019).
builder.Services.AddSingleton<IAuthorizationEngine, AuthorizationEngine>();

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
    using var scope = app.Services.CreateScope();
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = factory.CreateDbContext();
    SchemaBootstrap.Apply(db);
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

// Central authorization engine, run in shadow alongside the legacy role checks (ADR 0019).
var authzEngine = app.Services.GetRequiredService<IAuthorizationEngine>();

bool IsAdmin(HttpRequest req) =>
    !string.IsNullOrEmpty(adminToken) &&
    req.Headers.Authorization.ToString() == $"Bearer {adminToken}";

// --- health ---------------------------------------------------------------
// Liveness: the process is up (no dependencies checked).
app.MapGet("/health", () => Results.Json(new HealthResponse("ok", "control-plane-api", version)));

// Readiness: verifies the database is reachable, so an orchestrator never routes
// traffic to (or completes a rollout onto) a replica that cannot serve requests.
// Returns 503 when the DB is unreachable.
app.MapGet("/health/ready", async (IDbContextFactory<AppDbContext> factory) =>
{
    try
    {
        await using var db = await factory.CreateDbContextAsync();
        return await db.Database.CanConnectAsync()
            ? Results.Json(new HealthResponse("ready", "control-plane-api", version))
            : Results.Json(new HealthResponse("not-ready", "control-plane-api", version),
                statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        return Results.Json(new HealthResponse("not-ready", "control-plane-api", version),
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
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.TenantSettingsView)) return Results.Unauthorized();
    var tenant = store.FindTenant(tenantId);
    return tenant is null ? Results.NotFound() : Results.Json(tenant);
});

// Rename a tenant (owner only). Name is trimmed and length-bounded.
app.MapPost("/api/tenants/{tenantId}/rename", (string tenantId, RenameTenantRequest body, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.TenantRename)) return Results.Unauthorized();
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
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.TenantDeactivate)) return Results.Unauthorized();
    return store.DeactivateTenant(tenantId) ? Results.NoContent() : Results.NotFound();
});

// Reactivate a previously deactivated tenant.
app.MapPost("/api/tenants/{tenantId}/reactivate", (string tenantId, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.TenantReactivate)) return Results.Unauthorized();
    return store.ReactivateTenant(tenantId) ? Results.NoContent() : Results.NotFound();
});

// Issue a short-lived, single-use bootstrap token an operator hands to a gateway.
app.MapPost("/api/tenants/{tenantId}/enrollment-tokens", (string tenantId, IControlPlaneStore store, AuthService auth, MembershipService members, BillingService billing, HttpRequest req) =>
{
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.FleetGatewayEnroll)) return Results.Unauthorized();
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
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.FleetGatewayView)) return Results.Unauthorized();
    if (!store.TenantExists(tenantId)) return Results.NotFound();
    return Results.Json(store.GatewaysFor(tenantId));
});

// Decommission a gateway within a tenant: mark inactive and revoke its credential.
app.MapPost("/api/tenants/{tenantId}/gateways/{gatewayId}/decommission",
    (string tenantId, string gatewayId, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.FleetGatewayDecommission)) return Results.Unauthorized();
    return store.DecommissionGateway(tenantId, gatewayId) ? Results.NoContent() : Results.NotFound();
});

// Publish a (non-production) config version for a tenant's gateway.
app.MapPost("/api/tenants/{tenantId}/gateways/{gatewayId}/config",
    (string tenantId, string gatewayId, JsonElement settings, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.FleetConfigPublish)) return Results.Unauthorized();
    var view = store.PublishConfig(tenantId, gatewayId, settings.GetRawText());
    return view is null ? Results.NotFound() : Results.Json(view);
});

// Tenant audit log (any member role; platform admin).
app.MapGet("/api/tenants/{tenantId}/audit", (string tenantId, IControlPlaneStore store, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.AuditLogView)) return Results.Unauthorized();
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
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.MembersMemberView)) return Results.Unauthorized();
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
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.MembersMemberChangeRole)) return Results.Unauthorized();
    return ChangeOutcome(members.ChangeRole(tenantId, userId, body.Role, ActorRole(req, auth, members, tenantId)));
});

app.MapPost("/api/tenants/{tenantId}/members/{userId}/remove",
    (string tenantId, string userId, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.MembersMemberRemove)) return Results.Unauthorized();
    return ChangeOutcome(members.RemoveMember(tenantId, userId, ActorRole(req, auth, members, tenantId)));
});

app.MapPost("/api/tenants/{tenantId}/invitations",
    async (string tenantId, InviteRequest body, AuthService auth, MembershipService members, IEmailSender mail, HttpRequest req) =>
{
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.MembersMemberInvite)) return Results.Unauthorized();
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
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.MembersInvitationView)) return Results.Unauthorized();
    return Results.Json(members.InvitationsFor(tenantId));
});

app.MapPost("/api/tenants/{tenantId}/invitations/{invitationId}/revoke",
    (string tenantId, string invitationId, AuthService auth, MembershipService members, HttpRequest req) =>
{
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.MembersInvitationRevoke)) return Results.Unauthorized();
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

// --- identity: users + sessions (Phase C1) ---------------------------------
// Session resolution: `Authorization: Bearer ses_…` (SPA) or the `lc_session`
// HttpOnly cookie (same-site browser use). Cookie hardening to __Host- prefix +
// CSRF double-submit lands when the web app is served same-origin (Phase H).
(UserView User, string SessionId)? CurrentUser(HttpRequest req, AuthService auth)
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
// otherwise the session user's membership role in THAT tenant must satisfy the
// capability. Checked server-side on every tenant operation (no client claims).
bool AuthorizedInTenant(HttpRequest req, AuthService auth, MembershipService members,
    string tenantId, PermissionDefinition permission)
{
    if (IsAdmin(req))
    {
        return true;
    }
    var current = CurrentUser(req, auth);
    if (current is null)
    {
        return false;
    }
    var role = members.RoleIn(current.Value.User.Id, tenantId);
    if (role is null)
    {
        return false;
    }

    // Legacy decision — still the enforced one during the shadow phase (ADR 0019 §4).
    var legacy = LegacyCapabilityAllows(permission.Capability, role);

    // Shadow: evaluate the engine on the same (role, permission) and log any
    // disagreement. Step-up gates are treated as satisfied until session plumbing
    // lands, so this validates the endpoint→permission MAPPING against the legacy
    // predicate — a mismatch flags a mis-mapped endpoint. The engine does not gate
    // the request yet.
    var decision = authzEngine.Authorize(new AuthorizationRequest(
        [role], permission.Key, MfaSatisfied: true, FreshAuth: true, ApprovalGranted: true));
    if (decision.IsAllowed != legacy)
    {
        AuthzLog.ShadowMismatch(app.Logger, permission.Key, role, tenantId, legacy, decision.IsAllowed, decision.Reason);
    }
    return legacy;
}

// Bridges a permission's legacy capability to the existing Roles predicate — the
// behaviour still enforced while the engine runs in shadow.
static bool LegacyCapabilityAllows(LegacyCapability capability, string role) => capability switch
{
    LegacyCapability.View => Roles.CanView(role),
    LegacyCapability.ManageFleet => Roles.CanManageFleet(role),
    LegacyCapability.ManageUsers => Roles.CanManageUsers(role),
    LegacyCapability.ManageTenant => Roles.CanManageTenant(role),
    LegacyCapability.ManageBilling => Roles.CanManageBilling(role),
    _ => false,
};

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
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.BillingSubscriptionView)) return Results.Unauthorized();
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
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.BillingSubscriptionManage)) return Results.Unauthorized();
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
    if (!AuthorizedInTenant(req, auth, members, tenantId, Permissions.BillingPortalOpen)) return Results.Unauthorized();
    var customerId = billing.ProviderCustomerIdFor(tenantId);
    var redirect = await provider.CreatePortalAsync(tenantId, customerId);
    return Results.Json(new { url = redirect.Url });
});

// Provider webhook: the only unauthenticated write here, gated entirely by the
// provider's signature verification. Applied exactly once (idempotent + replay-
// safe via the billing_events unique index). Always 200 on a valid signature so
// the provider does not retry a duplicate we intentionally ignored.
app.MapPost("/api/billing/webhook", async (IBillingProvider provider, BillingService billing, HttpRequest req) =>
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
    if (string.IsNullOrEmpty(gatewayId) || !store.ValidateDeviceCredential(gatewayId, credential))
    {
        return Results.Unauthorized();
    }
    store.RecordHeartbeat(gatewayId);
    return Results.NoContent();
});

// A gateway reports PHI-free operational telemetry (message counts + last capture
// time), authenticated by its device credential. This also counts as a heartbeat.
// The payload carries no message content or result values — only counts.
app.MapPost("/api/gateways/telemetry", (GatewayTelemetryRequest body, IControlPlaneStore store, HttpRequest req) =>
{
    var gatewayId = req.Headers["X-Gateway-Id"].ToString();
    var credential = req.Headers["X-Gateway-Credential"].ToString();
    if (string.IsNullOrEmpty(gatewayId) || !store.ValidateDeviceCredential(gatewayId, credential))
    {
        return Results.Unauthorized();
    }
    // Clamp to non-negative; the edge reports counts, never negatives.
    var telemetry = new GatewayTelemetry(
        Math.Max(0, body.Captured), Math.Max(0, body.Pending),
        Math.Max(0, body.Delivered), Math.Max(0, body.Dead), body.LastCaptureAt);
    return store.RecordTelemetry(gatewayId, telemetry) ? Results.NoContent() : Results.NotFound();
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
    // An authenticated config fetch is also a liveness signal.
    store.RecordHeartbeat(gatewayId);
    var config = store.CurrentConfig(gatewayId);
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
internal sealed record HealthResponse(string Status, string Service, string Version);

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

/// <summary>Platform-admin membership grant (tenant bootstrap).</summary>
internal sealed record GrantMembershipRequest(string UserId, string TenantId, string Role);

/// <summary>Invite a user into a tenant with a role.</summary>
internal sealed record InviteRequest(string Email, string Role);

/// <summary>Change an existing member's role.</summary>
internal sealed record ChangeRoleRequest(string Role);

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
