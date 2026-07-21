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

/// <summary>Public view of a user (never includes the password hash or MFA secret).</summary>
public sealed record UserView(string Id, string Email, DateTimeOffset CreatedAt, bool EmailVerified, bool Active, bool MfaEnabled);

/// <summary>Result of a successful login: the one-time-shown session token.</summary>
public sealed record LoginResult(string SessionToken, DateTimeOffset ExpiresAt, UserView User);

/// <summary>Login outcome: either a session, or an MFA challenge to complete.</summary>
public sealed record LoginOutcome(bool MfaRequired, string? MfaToken, LoginResult? Session);

/// <summary>MFA enrollment material (secret shown once; QR URI for authenticators).</summary>
public sealed record MfaSetup(string Secret, string ProvisioningUri);

/// <summary>Public view of a session (never includes the token or its hash).</summary>
public sealed record SessionView(string Id, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset LastSeenAt, bool Current);

/// <summary>Users + sessions over the application database.</summary>
public sealed class AuthService
{
    /// <summary>Sentinel tenant id for platform-level (non-tenant) identity audit
    /// events in the shared audit table. Under RLS these rows are written with the
    /// tenant context bound to this value (see AuthScope below).</summary>
    public const string PlatformAuditTenant = "platform";

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
        db.Audit.Add(new AuditEntity { At = _clock.GetUtcNow(), Kind = kind, TenantId = PlatformAuditTenant, Detail = detail });

    // Identity writes touch global (non-tenant) tables, but the audit trail lives
    // in the RLS-protected `audit` table under the platform sentinel tenant. So any
    // method that writes (audit or otherwise) opens this scope, which binds
    // app.tenant_id to the sentinel for the transaction. IMPORTANT: every path that
    // calls SaveChanges must Complete() the scope, or the transaction rolls back.
    // Read-only paths and the per-request Authenticate() touch no RLS table and are
    // deliberately left unscoped.
    private static TenantScope PlatformAudit(AppDbContext db) => TenantScope.Begin(db, PlatformAuditTenant);

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
        using var scope = PlatformAudit(db);
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
        scope.Complete();
        return View(user);
    }

    /// <summary>
    /// Verify credentials. Null on any failure — unknown email, wrong password,
    /// or deactivated user are indistinguishable. An MFA-enabled account gets a
    /// short-lived challenge token instead of a session.
    /// </summary>
    public LoginOutcome? Login(string email, string password)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var normalized = NormalizeEmail(email);
        var user = db.Users.FirstOrDefault(u => u.Email == normalized);
        if (user is null || !user.Active)
        {
            Audit(db, "auth.login_failed", "invalid credentials");
            db.SaveChanges();
            scope.Complete();
            return null;
        }
        var check = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        if (check == PasswordVerificationResult.Failed)
        {
            Audit(db, "auth.login_failed", "invalid credentials");
            db.SaveChanges();
            scope.Complete();
            return null;
        }
        if (check == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = _hasher.HashPassword(user, password);
        }

        if (user.MfaEnabledAt is not null)
        {
            // Password is only the first factor; hand back a 5-minute challenge.
            var mfaToken = IssueToken(db, user.Id, "mfa", TimeSpan.FromMinutes(5));
            Audit(db, "auth.mfa_challenged", user.Id);
            db.SaveChanges();
            scope.Complete();
            return new LoginOutcome(true, mfaToken, null);
        }

        var result = CreateSession(db, user);
        db.SaveChanges();
        scope.Complete();
        return new LoginOutcome(false, null, result);
    }

    private LoginResult CreateSession(AppDbContext db, UserEntity user)
    {
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
        using var scope = PlatformAudit(db);
        var session = db.UserSessions.FirstOrDefault(s =>
            s.Id == sessionId && s.UserId == userId && s.RevokedAt == null);
        if (session is null)
        {
            return false;
        }
        session.RevokedAt = _clock.GetUtcNow();
        Audit(db, "auth.logout", userId);
        db.SaveChanges();
        scope.Complete();
        return true;
    }

    /// <summary>Revoke every active session for a user. Returns the count revoked.</summary>
    public int RevokeAllSessions(string userId)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var now = _clock.GetUtcNow();
        var sessions = db.UserSessions.Where(s => s.UserId == userId && s.RevokedAt == null).ToList();
        foreach (var s in sessions)
        {
            s.RevokedAt = now;
        }
        Audit(db, "auth.sessions_revoked", $"{userId} ({sessions.Count})");
        db.SaveChanges();
        scope.Complete();
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
        new(u.Id, u.Email, u.CreatedAt, u.EmailVerifiedAt is not null, u.Active, u.MfaEnabledAt is not null);

    // --- MFA: TOTP + recovery codes (Phase C4) ------------------------------

    /// <summary>Begin enrollment: store a pending secret (not armed until a code
    /// is proven). Null if MFA is already enabled.</summary>
    public MfaSetup? SetupMfa(string userId)
    {
        using var db = _factory.CreateDbContext();
        var user = db.Users.FirstOrDefault(u => u.Id == userId && u.Active);
        if (user is null || user.MfaEnabledAt is not null)
        {
            return null;
        }
        user.MfaSecret = Totp.NewSecret();
        db.SaveChanges();
        return new MfaSetup(user.MfaSecret, Totp.ProvisioningUri(user.MfaSecret, user.Email));
    }

    /// <summary>Arm MFA by proving a code from the enrolled authenticator.
    /// Returns the recovery codes (shown exactly once) or null.</summary>
    public IReadOnlyList<string>? EnableMfa(string userId, string code)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var user = db.Users.FirstOrDefault(u => u.Id == userId && u.Active);
        if (user?.MfaSecret is null || user.MfaEnabledAt is not null ||
            !Totp.Verify(user.MfaSecret, code, _clock.GetUtcNow()))
        {
            return null;
        }
        user.MfaEnabledAt = _clock.GetUtcNow();
        var codes = new List<string>(8);
        for (var i = 0; i < 8; i++)
        {
            var recovery = $"{Ids.NewSecret()[..5]}-{Ids.NewSecret()[..5]}";
            codes.Add(recovery);
            db.RecoveryCodes.Add(new RecoveryCodeEntity
            {
                Id = Ids.New("rc"),
                UserId = userId,
                CodeHash = HashToken(recovery),
            });
        }
        Audit(db, "auth.mfa_enabled", userId);
        db.SaveChanges();
        scope.Complete();
        return codes;
    }

    /// <summary>Disable MFA (requires a current TOTP code); removes recovery codes.</summary>
    public bool DisableMfa(string userId, string code)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var user = db.Users.FirstOrDefault(u => u.Id == userId && u.Active);
        if (user?.MfaSecret is null || user.MfaEnabledAt is null ||
            !Totp.Verify(user.MfaSecret, code, _clock.GetUtcNow()))
        {
            return false;
        }
        user.MfaSecret = null;
        user.MfaEnabledAt = null;
        db.RecoveryCodes.RemoveRange(db.RecoveryCodes.Where(r => r.UserId == userId));
        Audit(db, "auth.mfa_disabled", userId);
        db.SaveChanges();
        scope.Complete();
        return true;
    }

    /// <summary>Complete an MFA login challenge with a TOTP code.</summary>
    public LoginResult? VerifyMfaLogin(string mfaToken, string code)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var row = ConsumeToken(db, mfaToken, "mfa");
        if (row is null)
        {
            return null;
        }
        var user = db.Users.First(u => u.Id == row.UserId);
        if (user.MfaSecret is null || !Totp.Verify(user.MfaSecret, code, _clock.GetUtcNow()))
        {
            // The challenge token is consumed either way: a wrong code sends the
            // caller back through the password step rather than allowing retries.
            db.SaveChanges();
            scope.Complete();
            return null;
        }
        var result = CreateSession(db, user);
        db.SaveChanges();
        scope.Complete();
        return result;
    }

    /// <summary>Complete an MFA login challenge with a single-use recovery code.</summary>
    public LoginResult? RecoverMfaLogin(string mfaToken, string recoveryCode)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var row = ConsumeToken(db, mfaToken, "mfa");
        if (row is null)
        {
            return null;
        }
        var hash = HashToken(recoveryCode.Trim());
        var stored = db.RecoveryCodes.FirstOrDefault(r =>
            r.UserId == row.UserId && r.CodeHash == hash && r.UsedAt == null);
        if (stored is null)
        {
            db.SaveChanges();
            scope.Complete();
            return null;
        }
        stored.UsedAt = _clock.GetUtcNow();
        var user = db.Users.First(u => u.Id == row.UserId);
        Audit(db, "auth.mfa_recovery_used", user.Id);
        var result = CreateSession(db, user);
        db.SaveChanges();
        scope.Complete();
        return result;
    }

    // --- single-use account tokens (email verification, password reset) ----

    private string IssueToken(AppDbContext db, string userId, string purpose, TimeSpan ttl)
    {
        var token = $"{purpose[0]}tk_{Ids.NewSecret()}";
        var now = _clock.GetUtcNow();
        db.UserTokens.Add(new UserTokenEntity
        {
            Id = Ids.New("utk"),
            UserId = userId,
            Purpose = purpose,
            TokenHash = HashToken(token),
            CreatedAt = now,
            ExpiresAt = now.Add(ttl),
        });
        return token;
    }

    private UserTokenEntity? ConsumeToken(AppDbContext db, string token, string purpose)
    {
        var now = _clock.GetUtcNow();
        var row = db.UserTokens.FirstOrDefault(t =>
            t.TokenHash == HashToken(token) && t.Purpose == purpose && t.UsedAt == null && t.ExpiresAt > now);
        if (row is null)
        {
            return null;
        }
        row.UsedAt = now;
        return row;
    }

    /// <summary>Issue a 24h email-verification token for a user (email it to them).</summary>
    public (string Email, string Token)? IssueVerification(string userId)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var user = db.Users.AsNoTracking().FirstOrDefault(u => u.Id == userId && u.Active);
        if (user is null)
        {
            return null;
        }
        var token = IssueToken(db, userId, "verify", TimeSpan.FromHours(24));
        Audit(db, "auth.verification_sent", userId);
        db.SaveChanges();
        scope.Complete();
        return (user.Email, token);
    }

    /// <summary>Consume a verification token and mark the email verified.</summary>
    public bool VerifyEmail(string token)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var row = ConsumeToken(db, token, "verify");
        if (row is null)
        {
            return false;
        }
        var user = db.Users.First(u => u.Id == row.UserId);
        user.EmailVerifiedAt ??= _clock.GetUtcNow();
        Audit(db, "auth.email_verified", user.Id);
        db.SaveChanges();
        scope.Complete();
        return true;
    }

    /// <summary>Issue a 1h reset token if the email belongs to an active user.
    /// Null result must NOT change the caller's response (no account oracle).</summary>
    public (string Email, string Token)? IssuePasswordReset(string email)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var user = db.Users.AsNoTracking().FirstOrDefault(u => u.Email == NormalizeEmail(email) && u.Active);
        if (user is null)
        {
            return null;
        }
        var token = IssueToken(db, user.Id, "reset", TimeSpan.FromHours(1));
        Audit(db, "auth.reset_requested", user.Id);
        db.SaveChanges();
        scope.Complete();
        return (user.Email, token);
    }

    /// <summary>Consume a reset token, set the new password, revoke all sessions.</summary>
    public bool ResetPassword(string token, string newPassword)
    {
        using var db = _factory.CreateDbContext();
        using var scope = PlatformAudit(db);
        var row = ConsumeToken(db, token, "reset");
        if (row is null)
        {
            return false;
        }
        var user = db.Users.First(u => u.Id == row.UserId);
        user.PasswordHash = _hasher.HashPassword(user, newPassword);
        var now = _clock.GetUtcNow();
        foreach (var s in db.UserSessions.Where(s => s.UserId == user.Id && s.RevokedAt == null))
        {
            s.RevokedAt = now;
        }
        Audit(db, "auth.password_reset", user.Id);
        db.SaveChanges();
        scope.Complete();
        return true;
    }
}
