// Applies the control-plane schema on startup, including a safe adoption path for a
// database that predates migrations.
//
// - New database: Migrate() creates the schema and records migration history.
// - Database first created by an earlier EnsureCreated (schema present, but no
//   __EFMigrationsHistory): running Migrate() there would try to recreate existing
//   tables and fail. We first *baseline* it — record the already-present migrations
//   as applied — but ONLY when we detect that exact legacy state (history table
//   absent AND a known table present). Baselining never drops or alters data; it
//   just tells EF the current schema is already at the latest migration, which is
//   true because that schema was generated from the same model. Once any database
//   has a history table, this path is a no-op.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Startup schema application with legacy-database adoption.</summary>
public static class SchemaBootstrap
{
    // Informational only; EF decides applied/pending by MigrationId, not this value.
    private const string ProductVersion = "10.0.10";

    /// <summary>Baseline a pre-migrations database if needed, then apply migrations.</summary>
    public static void Apply(AppDbContext db)
    {
        if (IsLegacyEnsureCreated(db))
        {
            BaselineExistingSchema(db);
        }
        db.Database.Migrate();
    }

    // Legacy state: the schema exists (a known table is present) but migration
    // history has never been written. That is exactly what EnsureCreated leaves.
    private static bool IsLegacyEnsureCreated(AppDbContext db) =>
        !TableExists(db, "__EFMigrationsHistory") && TableExists(db, "tenants");

    private static bool TableExists(AppDbContext db, string table) =>
        db.Database
            .SqlQueryRaw<int>(
                "SELECT COUNT(*)::int AS \"Value\" FROM information_schema.tables " +
                "WHERE table_schema = 'public' AND table_name = {0}",
                table)
            .AsEnumerable()
            .First() > 0;

    private static void BaselineExistingSchema(AppDbContext db)
    {
        db.Database.ExecuteSqlRaw(
            "CREATE TABLE IF NOT EXISTS \"__EFMigrationsHistory\" (" +
            "\"MigrationId\" character varying(150) NOT NULL, " +
            "\"ProductVersion\" character varying(32) NOT NULL, " +
            "CONSTRAINT \"PK___EFMigrationsHistory\" PRIMARY KEY (\"MigrationId\"));");

        // The existing schema was generated from the current model, so every
        // migration this assembly defines is effectively already applied.
        foreach (var migrationId in db.Database.GetMigrations())
        {
            db.Database.ExecuteSqlRaw(
                "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") " +
                "VALUES ({0}, {1}) ON CONFLICT (\"MigrationId\") DO NOTHING;",
                migrationId, ProductVersion);
        }
    }
}
