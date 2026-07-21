// Tenant org hierarchy (P3, decision D4): tenant → site → laboratory →
// department. Scopes are the nodes a membership/role assignment can be pinned to
// and the level at which gateways/devices live. A role granted at a scope applies
// to that scope and everything below it — so the tree's containment relation
// (Contains) is the core primitive the scoped authorization engine will use.
//
// This is the model + tree logic. Persisting scopes (ScopeEntity), assigning roles
// to them, and making the AuthorizationEngine scope-aware are the next P3 slices.

namespace ControlPlane.Api;

/// <summary>The levels of the org hierarchy, shallow → deep. The numeric value is
/// the depth; a scope may contain another only if it is strictly shallower, so a
/// laboratory can sit directly under the tenant when a customer has no sites.</summary>
public enum ScopeType
{
    Tenant = 0,
    Site = 1,
    Laboratory = 2,
    Department = 3,
}

/// <summary>One node in a tenant's scope tree. The root is the tenant itself
/// (<see cref="ScopeType.Tenant"/>, no parent).</summary>
public sealed record ScopeNode(string Id, string TenantId, ScopeType Type, string Name, string? ParentId);

/// <summary>Scope-type rules shared by the model and the (future) engine.</summary>
public static class Scopes
{
    /// <summary>The type of a tenant's single root scope.</summary>
    public const ScopeType RootType = ScopeType.Tenant;

    /// <summary>Whether a parent scope may contain a child scope: only when the
    /// parent is strictly shallower (allows skipping levels, e.g. lab under tenant).</summary>
    public static bool CanContain(ScopeType parent, ScopeType child) => (int)parent < (int)child;
}

/// <summary>
/// An immutable, validated view of one tenant's scope tree, built from its flat
/// node set. Provides ancestry and containment — the relation scoped authorization
/// is defined against: a permission granted at scope S covers a resource at scope
/// R iff <c>Contains(S, R)</c>.
/// </summary>
public sealed class ScopeTree
{
    private readonly IReadOnlyDictionary<string, ScopeNode> _byId;

    private ScopeTree(IReadOnlyDictionary<string, ScopeNode> byId) => _byId = byId;

    /// <summary>The tenant these scopes belong to.</summary>
    public string TenantId { get; private init; } = "";

    /// <summary>The root (tenant) scope.</summary>
    public ScopeNode Root { get; private init; } = null!;

    /// <summary>
    /// Build and validate a tenant's scope tree. Requires: a single tenant id
    /// across all nodes; exactly one root (no parent) of type Tenant; every other
    /// node's parent to exist; each parent→child nesting to be valid
    /// (<see cref="Scopes.CanContain"/>); and every node to reach the root (no
    /// cycles or orphans). Throws <see cref="ArgumentException"/> on any violation.
    /// </summary>
    public static ScopeTree Build(IEnumerable<ScopeNode> nodes)
    {
        var list = nodes.ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("a scope tree needs at least the tenant root", nameof(nodes));
        }

        var byId = new Dictionary<string, ScopeNode>(StringComparer.Ordinal);
        foreach (var n in list)
        {
            if (!byId.TryAdd(n.Id, n))
            {
                throw new ArgumentException($"duplicate scope id '{n.Id}'", nameof(nodes));
            }
        }

        var tenantId = list[0].TenantId;
        if (list.Any(n => !string.Equals(n.TenantId, tenantId, StringComparison.Ordinal)))
        {
            throw new ArgumentException("all scopes in a tree must belong to one tenant", nameof(nodes));
        }

        var roots = list.Where(n => n.ParentId is null).ToList();
        if (roots.Count != 1 || roots[0].Type != Scopes.RootType)
        {
            throw new ArgumentException("a scope tree must have exactly one root of type Tenant", nameof(nodes));
        }

        foreach (var n in list)
        {
            if (n.ParentId is null)
            {
                continue;
            }
            if (!byId.TryGetValue(n.ParentId, out var parent))
            {
                throw new ArgumentException($"scope '{n.Id}' references unknown parent '{n.ParentId}'", nameof(nodes));
            }
            if (!Scopes.CanContain(parent.Type, n.Type))
            {
                throw new ArgumentException(
                    $"scope '{n.Id}' ({n.Type}) cannot nest under '{parent.Id}' ({parent.Type})", nameof(nodes));
            }
        }

        // Every node must reach the root by walking parents within N steps; a longer
        // walk means a cycle (CanContain makes true cycles impossible, but a
        // self/mutual parent slipping past type checks is still rejected here).
        foreach (var start in list)
        {
            var current = start;
            var steps = 0;
            while (current.ParentId is not null)
            {
                current = byId[current.ParentId];
                if (++steps > list.Count)
                {
                    throw new ArgumentException($"scope '{start.Id}' is in a cycle", nameof(nodes));
                }
            }
        }

        return new ScopeTree(byId) { TenantId = tenantId, Root = roots[0] };
    }

    /// <summary>Resolve a scope by id, or null if it is not in this tree.</summary>
    public ScopeNode? Find(string id) => _byId.GetValueOrDefault(id);

    /// <summary>The chain from a scope up to and including the root, nearest first.
    /// Empty if the id is unknown.</summary>
    public IReadOnlyList<ScopeNode> AncestorsAndSelf(string id)
    {
        var chain = new List<ScopeNode>();
        var current = Find(id);
        while (current is not null)
        {
            chain.Add(current);
            current = current.ParentId is null ? null : _byId[current.ParentId];
        }
        return chain;
    }

    /// <summary>Whether <paramref name="ancestorId"/> is <paramref name="descendantId"/>
    /// itself or an ancestor of it — i.e. whether a grant at the ancestor covers the
    /// descendant.</summary>
    public bool Contains(string ancestorId, string descendantId) =>
        AncestorsAndSelf(descendantId).Any(n => string.Equals(n.Id, ancestorId, StringComparison.Ordinal));

    /// <summary>All scopes at or below <paramref name="id"/> (including it).</summary>
    public IReadOnlyCollection<ScopeNode> DescendantsAndSelf(string id) =>
        _byId.Values.Where(n => Contains(id, n.Id)).ToList();

    /// <summary>The materialized path of ids from the root to a scope, inclusive,
    /// e.g. <c>/scp_root/scp_site/scp_lab</c>. Enables prefix descendant queries.</summary>
    public string PathOf(string id)
    {
        var fromRoot = AncestorsAndSelf(id).Reverse();
        return "/" + string.Join("/", fromRoot.Select(n => n.Id));
    }
}
