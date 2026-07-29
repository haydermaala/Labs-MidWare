// Source-generated startup log messages. These live outside Program.cs because a
// top-level program cannot host `[LoggerMessage]` partial methods, and the analyzers
// (CA1848/CA1873) require the generated delegates rather than ILogger extension calls.

using Microsoft.Extensions.Logging;

namespace ControlPlane.Api;

/// <summary>Startup diagnostics that must be greppable in deploy logs.</summary>
public static partial class StartupLog
{
    /// <summary>
    /// A missing DATABASE_URL outside Development silently drops the app onto the EF
    /// in-memory provider, where it serves an EMPTY database while looking healthy.
    /// This is the loudest possible signal that the deploy is misconfigured.
    /// </summary>
    [LoggerMessage(
        Level = LogLevel.Critical,
        Message = "DATABASE_URL is not configured in the {Environment} environment — the app is on "
                + "the in-memory fallback and is serving an EMPTY database. Readiness reports not-ready.")]
    public static partial void InMemoryFallbackInNonDevelopment(ILogger logger, string environment);
}
