// Control-plane API — Phase 1 scaffold.
// Exposes liveness/readiness health endpoints only. No tenant, driver, mapping,
// or clinical endpoints exist yet; those arrive in Phase 8 per DEVELOPMENT_PLAN.md.
// Health payloads must never contain PHI, result values, or secrets.

using System.Reflection;

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

// Liveness: process is up.
app.MapGet("/health", () => Results.Json(new HealthResponse("ok", "control-plane-api", version)));

// Readiness: dependencies (DB, etc.) are reachable. Phase 1 has no dependencies,
// so readiness mirrors liveness until Phase 8 wires PostgreSQL and checks.
app.MapGet("/health/ready", () => Results.Json(new HealthResponse("ready", "control-plane-api", version)));

app.Run();

/// <summary>Minimal, PHI-free health payload.</summary>
internal sealed record HealthResponse(string Status, string Service, string Version);

// Exposed so integration tests can host the app via WebApplicationFactory.
public partial class Program;
