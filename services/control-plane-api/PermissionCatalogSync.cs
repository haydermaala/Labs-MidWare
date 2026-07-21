// Reconciles the permission_definitions table with the authoritative code catalog
// (Permissions.All) at startup — the same "schema by migration, data by startup
// reconcile" pattern as SchemaBootstrap. Keeping the seed here (not hardcoded in a
// migration) means the migration stays an immutable schema change while the table
// always mirrors the current catalog: new permissions are inserted, changed
// metadata is updated, and permissions removed from the catalog are soft-retired
// (Active=false, kept for history/referential integrity). Idempotent.

namespace ControlPlane.Api;

/// <summary>Startup reconciliation of the permission-definitions mirror table.</summary>
public static class PermissionCatalogSync
{
    /// <summary>Upsert every catalog permission and retire any that no longer exist.
    /// Returns the number of rows inserted, updated, or retired.</summary>
    public static int Apply(AppDbContext db)
    {
        var existing = db.PermissionDefinitions.ToDictionary(p => p.Key, StringComparer.Ordinal);
        var catalogKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var p in Permissions.All)
        {
            catalogKeys.Add(p.Key);
            if (!existing.TryGetValue(p.Key, out var row))
            {
                row = new PermissionDefinitionEntity { Key = p.Key };
                db.PermissionDefinitions.Add(row);
            }

            row.Domain = p.Domain;
            row.Resource = p.Resource;
            row.Action = p.Action;
            row.Risk = p.Risk.ToString();
            row.Capability = p.Capability.ToString();
            row.RequiresMfa = p.RequiresMfa;
            row.RequiresFreshAuth = p.RequiresFreshAuth;
            row.RequiresApproval = p.RequiresApproval;
            row.Delegable = p.Delegable;
            row.Description = p.Description;
            row.Active = true;
        }

        // Soft-retire rows whose permission left the catalog (keep the row for
        // history and so existing grants referencing it are not orphaned).
        foreach (var (key, row) in existing)
        {
            if (!catalogKeys.Contains(key) && row.Active)
            {
                row.Active = false;
            }
        }

        return db.SaveChanges();
    }
}
