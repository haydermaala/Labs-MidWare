// Scope-tree persistence (P3, ADR 0020). Manages a tenant's org hierarchy over the
// scopes table, independent of IControlPlaneStore (scopes are a Postgres-backed
// feature, accessed like BillingService/MembershipService). Every tenant gets a
// single root scope; children are added under a valid parent with a maintained
// materialized Path. Reads build a validated ScopeTree for the authorization
// engine.

using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api;

/// <summary>Creates and reads a tenant's scope hierarchy.</summary>
public sealed class ScopeService
{
    private readonly IDbContextFactory<AppDbContext> _factory;
    private readonly TimeProvider _clock;

    public ScopeService(IDbContextFactory<AppDbContext> factory, TimeProvider clock)
    {
        _factory = factory;
        _clock = clock;
    }

    private static ScopeNode ToNode(ScopeEntity s) =>
        new(s.Id, s.TenantId, Enum.Parse<ScopeType>(s.Type), s.Name, s.ParentId);

    /// <summary>The tenant's single root (Tenant) scope, creating it if absent.
    /// Idempotent — safe to call on every tenant-create.</summary>
    public ScopeEntity EnsureRoot(string tenantId, string name)
    {
        using var db = _factory.CreateDbContext();
        var root = db.Scopes.FirstOrDefault(s => s.TenantId == tenantId && s.ParentId == null);
        if (root is not null)
        {
            return root;
        }
        root = new ScopeEntity
        {
            Id = Ids.New("scp"),
            TenantId = tenantId,
            Type = ScopeType.Tenant.ToString(),
            Name = name,
            ParentId = null,
            CreatedAt = _clock.GetUtcNow(),
        };
        root.Path = "/" + root.Id;
        db.Scopes.Add(root);
        db.SaveChanges();
        return root;
    }

    /// <summary>Add a child scope under <paramref name="parentId"/>. Returns null if
    /// the parent is unknown in the tenant or the nesting is invalid (the parent must
    /// be strictly shallower). Maintains the materialized <see cref="ScopeEntity.Path"/>.</summary>
    public ScopeEntity? CreateChild(string tenantId, string parentId, ScopeType type, string name)
    {
        using var db = _factory.CreateDbContext();
        var parent = db.Scopes.FirstOrDefault(s => s.Id == parentId && s.TenantId == tenantId);
        if (parent is null || !Scopes.CanContain(Enum.Parse<ScopeType>(parent.Type), type))
        {
            return null;
        }
        var child = new ScopeEntity
        {
            Id = Ids.New("scp"),
            TenantId = tenantId,
            Type = type.ToString(),
            Name = name,
            ParentId = parentId,
            CreatedAt = _clock.GetUtcNow(),
        };
        child.Path = parent.Path + "/" + child.Id;
        db.Scopes.Add(child);
        db.SaveChanges();
        return child;
    }

    /// <summary>A tenant's scopes as a validated <see cref="ScopeTree"/>, or null if
    /// the tenant has no scopes yet (root not created).</summary>
    public ScopeTree? Tree(string tenantId)
    {
        using var db = _factory.CreateDbContext();
        var nodes = db.Scopes.AsNoTracking()
            .Where(s => s.TenantId == tenantId)
            .AsEnumerable()
            .Select(ToNode)
            .ToList();
        return nodes.Count == 0 ? null : ScopeTree.Build(nodes);
    }
}
