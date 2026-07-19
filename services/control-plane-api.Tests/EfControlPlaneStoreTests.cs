using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>
/// Behavioural tests for the EF Core store, run against the EF in-memory provider.
/// They assert the same tenant-isolation and enrollment contract as the in-memory
/// store so the PostgreSQL-backed deployment behaves identically to local dev.
/// </summary>
public sealed class EfControlPlaneStoreTests
{
    /// <summary>Minimal factory over a fixed in-memory database name.</summary>
    private sealed class TestFactory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public TestFactory(string dbName) =>
            _options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options;
        public AppDbContext CreateDbContext() => new(_options);
    }

    // A fresh, isolated database per store keeps tests independent (unique name).
    private static EfControlPlaneStore NewStore() =>
        new(new TestFactory($"cp_{Guid.NewGuid():N}"), TimeProvider.System);

    [Fact]
    public void CreateTenant_Persists_And_Lists()
    {
        var store = NewStore();
        var a = store.CreateTenant("Lab A");
        var b = store.CreateTenant("Lab B");

        var all = store.Tenants();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, t => t.Id == a.Id && t.Name == "Lab A");
        Assert.Contains(all, t => t.Id == b.Id && t.Name == "Lab B");
        Assert.True(store.TenantExists(a.Id));
        Assert.False(store.TenantExists("ten_missing"));
    }

    [Fact]
    public void Enroll_Creates_Gateway_Scoped_To_Its_Tenant()
    {
        var store = NewStore();
        var tenantA = store.CreateTenant("Lab A");
        var tenantB = store.CreateTenant("Lab B");

        var token = store.IssueBootstrapToken(tenantA.Id, TimeSpan.FromMinutes(15));
        Assert.NotNull(token);

        var enrolled = store.Enroll(token!.Token, "edge-1");
        Assert.NotNull(enrolled);
        Assert.Equal(tenantA.Id, enrolled!.TenantId);
        Assert.False(string.IsNullOrEmpty(enrolled.DeviceCredential));

        var aGateways = store.GatewaysFor(tenantA.Id);
        var bGateways = store.GatewaysFor(tenantB.Id);
        Assert.Single(aGateways);
        Assert.Equal(enrolled.GatewayId, aGateways.First().Id);
        Assert.Empty(bGateways);
    }

    [Fact]
    public void Bootstrap_Token_Is_Single_Use()
    {
        var store = NewStore();
        var tenant = store.CreateTenant("Lab C");
        var token = store.IssueBootstrapToken(tenant.Id, TimeSpan.FromMinutes(15));

        Assert.NotNull(store.Enroll(token!.Token, "edge-1"));
        Assert.Null(store.Enroll(token.Token, "edge-2"));
    }

    [Fact]
    public void Expired_Bootstrap_Token_Is_Rejected()
    {
        var store = NewStore();
        var tenant = store.CreateTenant("Lab D");
        var token = store.IssueBootstrapToken(tenant.Id, TimeSpan.FromMinutes(-1));

        Assert.Null(store.Enroll(token!.Token, "edge-1"));
    }

    [Fact]
    public void IssueBootstrapToken_For_Unknown_Tenant_Is_Null()
    {
        var store = NewStore();
        Assert.Null(store.IssueBootstrapToken("ten_missing", TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void DeviceCredential_Validates_Only_The_Issued_Secret()
    {
        var store = NewStore();
        var tenant = store.CreateTenant("Lab E");
        var token = store.IssueBootstrapToken(tenant.Id, TimeSpan.FromMinutes(15));
        var enrolled = store.Enroll(token!.Token, "edge-1")!;

        Assert.True(store.ValidateDeviceCredential(enrolled.GatewayId, enrolled.DeviceCredential));
        Assert.False(store.ValidateDeviceCredential(enrolled.GatewayId, "wrong"));
        Assert.False(store.ValidateDeviceCredential("gw_missing", enrolled.DeviceCredential));
    }

    [Fact]
    public void PublishConfig_Versions_And_Is_Tenant_Scoped()
    {
        var store = NewStore();
        var tenantA = store.CreateTenant("Lab A");
        var tenantB = store.CreateTenant("Lab B");
        var token = store.IssueBootstrapToken(tenantA.Id, TimeSpan.FromMinutes(15));
        var gw = store.Enroll(token!.Token, "edge-1")!;

        var v1 = store.PublishConfig(tenantA.Id, gw.GatewayId, "{\"poll\":1}");
        Assert.NotNull(v1);
        Assert.Equal(1, v1!.Version);
        Assert.Equal("non-production", v1.Environment);

        var v2 = store.PublishConfig(tenantA.Id, gw.GatewayId, "{\"poll\":2}");
        Assert.Equal(2, v2!.Version);

        // A different tenant cannot publish to this gateway.
        Assert.Null(store.PublishConfig(tenantB.Id, gw.GatewayId, "{\"poll\":9}"));

        var current = store.CurrentConfig(gw.GatewayId);
        Assert.Equal(2, current!.Version);
        Assert.Equal("{\"poll\":2}", current.SettingsJson);
    }

    [Fact]
    public void CurrentConfig_Is_Null_When_None_Published()
    {
        var store = NewStore();
        Assert.Null(store.CurrentConfig("gw_missing"));
    }

    [Fact]
    public void Audit_Is_Tenant_Scoped_And_Ordered()
    {
        var store = NewStore();
        var tenantA = store.CreateTenant("Lab A");
        var tenantB = store.CreateTenant("Lab B");
        var token = store.IssueBootstrapToken(tenantA.Id, TimeSpan.FromMinutes(15));
        store.Enroll(token!.Token, "edge-1");

        var aAudit = store.AuditFor(tenantA.Id);
        var bAudit = store.AuditFor(tenantB.Id);

        Assert.Contains(aAudit, e => e.Kind == "tenant.created");
        Assert.Contains(aAudit, e => e.Kind == "enrollment.token_issued");
        Assert.Contains(aAudit, e => e.Kind == "gateway.enrolled");
        Assert.All(aAudit, e => Assert.Equal(tenantA.Id, e.TenantId));
        // Tenant B only has its own creation event; none of A's leak across.
        Assert.All(bAudit, e => Assert.Equal(tenantB.Id, e.TenantId));
    }

    [Fact]
    public void New_Tenant_And_Gateway_Are_Active()
    {
        var store = NewStore();
        var tenant = store.CreateTenant("Lab A");
        Assert.True(tenant.Active);
        Assert.True(store.Tenants().Single(t => t.Id == tenant.Id).Active);

        var token = store.IssueBootstrapToken(tenant.Id, TimeSpan.FromMinutes(15));
        var gw = store.Enroll(token!.Token, "edge-1")!;
        Assert.True(store.GatewaysFor(tenant.Id).Single(g => g.Id == gw.GatewayId).Active);
    }

    [Fact]
    public void Deactivated_Tenant_Cannot_Issue_Tokens_Until_Reactivated()
    {
        var store = NewStore();
        var tenant = store.CreateTenant("Lab A");

        Assert.True(store.DeactivateTenant(tenant.Id));
        Assert.False(store.Tenants().Single(t => t.Id == tenant.Id).Active);
        Assert.Null(store.IssueBootstrapToken(tenant.Id, TimeSpan.FromMinutes(15)));

        Assert.True(store.ReactivateTenant(tenant.Id));
        Assert.True(store.Tenants().Single(t => t.Id == tenant.Id).Active);
        Assert.NotNull(store.IssueBootstrapToken(tenant.Id, TimeSpan.FromMinutes(15)));
    }

    [Fact]
    public void Deactivating_Tenant_After_Token_Issued_Blocks_Enrollment()
    {
        var store = NewStore();
        var tenant = store.CreateTenant("Lab A");
        var token = store.IssueBootstrapToken(tenant.Id, TimeSpan.FromMinutes(15));

        store.DeactivateTenant(tenant.Id);
        Assert.Null(store.Enroll(token!.Token, "edge-1"));
    }

    [Fact]
    public void Deactivate_Unknown_Tenant_Is_False()
    {
        var store = NewStore();
        Assert.False(store.DeactivateTenant("ten_missing"));
        Assert.False(store.ReactivateTenant("ten_missing"));
    }

    [Fact]
    public void Decommission_Marks_Inactive_And_Revokes_Credential()
    {
        var store = NewStore();
        var tenant = store.CreateTenant("Lab A");
        var token = store.IssueBootstrapToken(tenant.Id, TimeSpan.FromMinutes(15));
        var gw = store.Enroll(token!.Token, "edge-1")!;
        Assert.True(store.ValidateDeviceCredential(gw.GatewayId, gw.DeviceCredential));

        Assert.True(store.DecommissionGateway(tenant.Id, gw.GatewayId));

        // Credential revoked, gateway shows inactive but is still listed (audit/history).
        Assert.False(store.ValidateDeviceCredential(gw.GatewayId, gw.DeviceCredential));
        var view = store.GatewaysFor(tenant.Id).Single(g => g.Id == gw.GatewayId);
        Assert.False(view.Active);
    }

    [Fact]
    public void Decommission_Is_Tenant_Scoped_And_Handles_Unknown()
    {
        var store = NewStore();
        var tenantA = store.CreateTenant("Lab A");
        var tenantB = store.CreateTenant("Lab B");
        var token = store.IssueBootstrapToken(tenantA.Id, TimeSpan.FromMinutes(15));
        var gw = store.Enroll(token!.Token, "edge-1")!;

        // Another tenant cannot decommission this gateway.
        Assert.False(store.DecommissionGateway(tenantB.Id, gw.GatewayId));
        Assert.False(store.DecommissionGateway(tenantA.Id, "gw_missing"));
        // The credential is untouched by the failed cross-tenant attempt.
        Assert.True(store.ValidateDeviceCredential(gw.GatewayId, gw.DeviceCredential));
    }
}
