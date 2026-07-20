// Identity foundations (LabConnect Phase C): users, password verification, and
// server-side sessions.
//
// Security posture:
// - Passwords are hashed with ASP.NET Core Identity's PasswordHasher (PBKDF2-
//   SHA256, per-hash salt, v3 format) — never stored or logged in clear.
// - Session tokens are 256-bit random values shown once; only their SHA-256
//   hash is persisted, so a database leak does not yield usable sessions.
// - Login failures are indistinguishable (same generic result for unknown
//   email vs wrong password); success/failure is audited under the "platform"
//   scope without recording the attempted email on failure (no user enumeration
//   via the audit trail either).
// - Auth always runs on EF (Npgsql in deployments, the EF in-memory provider in
//   local/dev/tests), independent of which IControlPlaneStore backs the fleet.

using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;

namespace ControlPlane.Api;

/// <summary>Public view of a user (never includes the password hash).</summary>
public sealed record UserView(string Id, string Email, DateTimeOffset CreatedAt, bool EmailVerified, bool Active);

/// <summary>Result of a successful login: the one-time-shown session token.</summary>
public sealed record LoginResult(string SessionToken, DateTimeOffset ExpiresAt, UserView User);

/// <summary>Public view of a session (never includes the token or its hash).</summary>
public sealed record SessionView(string Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset LastSeenAt, bool Current);

/// <summary>Users + sessions over the application database.</summary>
public sealed class AuthService
{
    /// <summary>Audit scope for platform-level (non-tenant) identity events.</summary>
    public const string PlatformScope = "platform";

    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(7);

    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;
    private readonly PasswordHasher<UserEntity> _hasher = new();

    public AuthService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    private static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();

    private void Audit(AppDbContext db, string kind, string detail) =>
        db.Audit.Add(new AuditEntity { At = _clock.GetUtcNow(), Kind = kind, TenantId = PlatformScope, Detail = detail });

    /// <summary>Basic email shape check (full verification is by delivered link).</summary>
    public static bool LooksLikeEmail(string email)
    {
        var e = email.Trim();
        var at = e.IndexOf('@', StringComparison.Ordinal);
        return at > 0 && at < e.Length - 3 && e.IndexOf('.', at) > at + 1 && !e.Contains(' ', StringComparison.Ordinal);
    }

    /// <summary>Minimum password policy: length only (no composition rules, per NIST).</summary>
    public static bool PasswordAcceptable(string password) => password.Length is >= 12 and <= 256;

    /// <summary>Create a user. Returns null if the email is already registered.</summary>
    public UserView? CreateUser(string email, string password)
    {
        using var db = _factory.CreateDbContext();
        var normalized = NormalizeEmail(email);
        if (db.Users.AsNoTracking().Any(u => u.Email == normalized))
        {
            return null;
        }
        var user = new UserEntity
        {
            Id = Ids.New("usr"),
            Email = normalized,
            CreatedAt = _clock.GetUtcNow(),
            Active = true,
        };
        user.PasswordHash = _hasher.HashPassword(user, password);
        db.Users.Add(user);
        Audit(db, "user.created", user.Id);
        db.SaveChanges();
        return View(user);
    }

    /// <summary>
    /// Verify credentials and open a session. Null on any failure — unknown
    /// email, wrong password, or deactivated user are indistinguishable.
    /// </summary>
    public LoginResult? Login(string email, string password)
    {
        using var db = _factory.CreateDbContext();
        var normalized = NormalizeEmail(email);
        var user = db.Users.FirstOrDefault(u => u.Email == normalized);
        if (user is null || !user.Active)
        {
            Audit(db, "auth.login_failed", "invalid credentials");
            db.SaveChanges();
            return null;
        }
        var check = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (check == PasswordVerificationResult.Failed)
        {
            Audit(db, "auth.login_failed", "invalid credentials");
            db.SaveChanges();
            return null;
        }
        if (check == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
        }

        var token = "ses_" + Ids.NewSecret();
        var now = _clock.GetUtcNow();
        var session = new UserSessionEntity
        {
            Id = Ids.New("ses"),
            UserId = user.Id,
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = now.Add(SessionLifetime),
            LastSeenAt = now,
        };
        db.UserSessions.Add(session);
        Audit(db, "auth.login", user.Id);
        db.SaveChanges();
        return new LoginResult(token, session.ExpiresAt, View(user));
    }

    /// <summary>Resolve a session token to its user; null if unknown/expired/revoked.</summary>
    public (UserView User, string SessionId)? Authenticate(string sessionToken)
    {
        using var db = _factory.CreateDbContext();
        var hash = HashToken(sessionToken);
        var now = _clock.GetUtcNow();
        var session = db.UserSessions.FirstOrDefault(s =>
            s.TokenHash == hash && s.RevokedAt == null && s.ExpiresAt > now);
        if (session is null)
        {
            return null;
        }
        var user = db.Users.AsNoTracking().FirstOrDefault(u => u.Id == session.UserId && u.Active);
        if (user is null)
        {
            return null;
        }
        // Touch last-seen at most once a minute to avoid a write per request.
        if (now - session.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            session.LastSeenAt = now;
            db.SaveChanges();
        }
        return (View(user), session.Id);
    }

    /// <summary>Revoke one session (logout). True if it existed and was active.</summary>
    public bool RevokeSession(string userId, string sessionId)
    {
        using var db = _factory.CreateDbContext();
        var session = db.UserSessions.FirstOrDefault(s =>
            s.Id == sessionId && s.UserId == userId && s.RevokedAt == null);
        if (session is null)
        {
            return false;
        }
        session.RevokedAt = _clock.GetUtcNow();
        Audit(db, "auth.logout", userId);
        db.SaveChanges();
        return true;
    }

    /// <summary>Revoke every active session for a user. Returns the count revoked.</summary>
    public int RevokeAllSessions(string userId)
    {
        using var db = _factory.CreateDbContext();
        var now = _clock.GetUtcNow();
        var sessions = db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToList();
        foreach (var s in sessions)
        {
            s.RevokedAt = now;
        }
        Audit(db, "auth.sessions_revoked", $"{userId} ({sessions.Count})");
        db.SaveChanges();
        return sessions.Count;
    }

    /// <summary>A user's sessions, newest first (active and recently revoked).</summary>
    public IReadOnlyCollection<SessionView> SessionsFor(string userId, string currentSessionId)
    {
        using var db = _factory.CreateDbContext();
        var now = _clock.GetUtcNow();
        return db.UserSessions.AsNoTracking()
            .Where(s => s.UserId == userId && s.RevokedAt == null && s.ExpiresAt > now)
            .OrderByDescending(s => s.CreatedAt)
            .AsEnumerable()
            .Select(s => new SessionView(s.Id, s.CreatedAt, s.ExpiresAt, s.LastSeenAt, s.Id == currentSessionId))
            .ToList();
    }

    private static UserView View(UserEntity u) =>
        new(u.Id, u.Email, u.CreatedAt, u.EmailVerifiedAt is not null, u.Active);
}
