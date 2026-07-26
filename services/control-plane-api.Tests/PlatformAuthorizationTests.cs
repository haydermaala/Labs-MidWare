using ControlPlane.Api;

namespace ControlPlane.Api.Tests;

/// <summary>P6 platform (super-admin) authorization: the named-role matrix replaces
/// the single god-mode admin token. Load-bearing properties: deny-by-default, no
/// routine all-powerful role, break-glass Root always high-assurance, and platform
/// access is disjoint from tenant roles.</summary>
public sealed class PlatformAuthorizationTests
{
    private static readonly PlatformAuthorizationEngine Engine = new();

    private static AuthorizationResult Decide(
        string role, string permissionKey, bool mfa = true, bool fresh = true) =>
        Engine.Authorize(new PlatformAuthorizationRequest([role], permissionKey, mfa, fresh));

    [Fact]
    public void Catalog_Keys_Are_Unique_And_Well_Formed()
    {
        Assert.NotEmpty(PlatformPermissions.All);
        Assert.Equal(
            PlatformPermissions.All.Count,
            PlatformPermissions.All.Select(p => p.Key).Distinct(StringComparer.Ordinal).Count());
        foreach (var p in PlatformPermissions.All)
        {
            Assert.Equal($"platform.{p.Resource}.{p.Action}", p.Key);
            Assert.False(string.IsNullOrWhiteSpace(p.Description));
            Assert.Same(p, PlatformPermissions.Find(p.Key));
        }
    }

    [Fact]
    public void Matrix_Only_References_Catalog_Permissions()
    {
        foreach (var role in PlatformRoles.All)
        {
            foreach (var key in PlatformRolePermissions.PermissionsOf(role))
            {
                Assert.NotNull(PlatformPermissions.Find(key));
            }
        }
    }

    [Fact]
    public void Root_Owner_Grants_Everything_But_Only_With_High_Assurance()
    {
        // Root holds every permission…
        foreach (var p in PlatformPermissions.All)
        {
            Assert.True(PlatformRolePermissions.Grants(PlatformRoles.RootOwner, p.Key), p.Key);
        }
        // …but even a Low-risk permission (no per-permission step-up) demands MFA+fresh
        // via break-glass elevation.
        var low = PlatformPermissions.TenantRead.Key;
        Assert.True(Decide(PlatformRoles.RootOwner, low, mfa: true, fresh: true).IsAllowed);
        Assert.True(Decide(PlatformRoles.RootOwner, low, mfa: false, fresh: true).RequiresStepUp);
        Assert.True(Decide(PlatformRoles.RootOwner, low, mfa: true, fresh: false).RequiresStepUp);
    }

    [Fact]
    public void No_Single_Routine_Role_Is_All_Powerful()
    {
        // Every non-break-glass role is missing at least one permission Root has.
        foreach (var role in PlatformRoles.All.Where(r => !PlatformRoles.IsBreakGlass(r)))
        {
            var held = PlatformRolePermissions.PermissionsOf(role);
            Assert.True(held.Count < PlatformPermissions.All.Count, $"{role} is all-powerful");
        }
    }

    [Fact]
    public void Least_Privilege_Boundaries_Hold()
    {
        // Billing manages subscriptions but cannot provision or offboard tenants.
        Assert.True(Decide(PlatformRoles.BillingAdmin, PlatformPermissions.SubscriptionManage.Key).IsAllowed);
        Assert.False(Decide(PlatformRoles.BillingAdmin, PlatformPermissions.TenantProvision.Key).IsAllowed);
        Assert.False(Decide(PlatformRoles.BillingAdmin, PlatformPermissions.TenantOffboard.Key).IsAllowed);

        // Auditor is read-only: no release management, no support approval.
        Assert.True(Decide(PlatformRoles.Auditor, PlatformPermissions.AuditRead.Key).IsAllowed);
        Assert.False(Decide(PlatformRoles.Auditor, PlatformPermissions.ReleaseManage.Key).IsAllowed);

        // Release manager only manages releases.
        Assert.True(Decide(PlatformRoles.ReleaseManager, PlatformPermissions.ReleaseManage.Key).IsAllowed);
        Assert.False(Decide(PlatformRoles.ReleaseManager, PlatformPermissions.TenantRead.Key).IsAllowed);

        // Support can request but NOT approve a support grant (dynamic SoD wiring later).
        Assert.True(Decide(PlatformRoles.SupportEngineer, PlatformPermissions.SupportRequest.Key).IsAllowed);
        Assert.False(Decide(PlatformRoles.SupportEngineer, PlatformPermissions.SupportApprove.Key).IsAllowed);
        Assert.True(Decide(PlatformRoles.SecurityAdmin, PlatformPermissions.SupportApprove.Key).IsAllowed);
    }

    [Fact]
    public void Deny_By_Default_And_Step_Up()
    {
        // Unknown permission / no role / role lacking the permission.
        Assert.False(Engine.Authorize(new PlatformAuthorizationRequest([PlatformRoles.Auditor], "platform.bogus.read")).IsAllowed);
        Assert.False(Engine.Authorize(new PlatformAuthorizationRequest([], PlatformPermissions.AuditRead.Key)).IsAllowed);

        // A fresh-auth permission denies (as step-up) without fresh auth.
        var d = Decide(PlatformRoles.OperationsAdmin, PlatformPermissions.TenantSuspend.Key, mfa: true, fresh: false);
        Assert.False(d.IsAllowed);
        Assert.True(d.RequiresStepUp);
    }

    [Fact]
    public void Platform_Access_Is_Disjoint_From_Tenant_Roles()
    {
        // A TENANT role (owner) is not a platform role and grants no platform access.
        Assert.False(PlatformRolePermissions.Grants(Roles.Owner, PlatformPermissions.TenantRead.Key));
        Assert.False(Decide(Roles.Owner, PlatformPermissions.TenantRead.Key).IsAllowed);
    }
}
