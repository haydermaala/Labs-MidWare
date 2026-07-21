using ControlPlane.Api;
using Microsoft.EntityFrameworkCore;

namespace ControlPlane.Api.Tests;

/// <summary>
/// PermissionCatalogSync reconciles the permission_definitions mirror table with
/// the authoritative code catalog. Separate contexts over one in-memory database
/// model the startup path (query existing → upsert → save).
/// </summary>
public sealed class PermissionCatalogSyncTests
{
    private static DbContextOptions<AppDbContext> NewDb() =>
        new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"perm_{Guid.NewGuid():N}").Options;

    [Fact]
    public void Apply_Seeds_The_Table_With_Exactly_The_Catalog()
    {
        var opts = NewDb();
        using (var db = new AppDbContext(opts))
        {
            Assert.Equal(Permissions.All.Count, PermissionCatalogSync.Apply(db));
        }

        using (var db = new AppDbContext(opts))
        {
            var rows = db.PermissionDefinitions.ToList();
            Assert.Equal(
                Permissions.All.Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal),
                rows.Select(r => r.Key).OrderBy(k => k, StringComparer.Ordinal));
            Assert.All(rows, r => Assert.True(r.Active));
        }
    }

    [Fact]
    public void Apply_Is_Idempotent()
    {
        var opts = NewDb();
        using (var db = new AppDbContext(opts))
        {
            Assert.True(PermissionCatalogSync.Apply(db) > 0);
        }
        using (var db = new AppDbContext(opts))
        {
            Assert.Equal(0, PermissionCatalogSync.Apply(db)); // nothing changed on the second pass
        }
        using (var db = new AppDbContext(opts))
        {
            Assert.Equal(Permissions.All.Count, db.PermissionDefinitions.Count());
        }
    }

    [Fact]
    public void Apply_Soft_Retires_Permissions_No_Longer_In_The_Catalog()
    {
        var opts = NewDb();
        using (var db = new AppDbContext(opts))
        {
            db.PermissionDefinitions.Add(new PermissionDefinitionEntity { Key = "legacy.thing.gone", Active = true });
            db.SaveChanges();
        }

        using (var db = new AppDbContext(opts))
        {
            PermissionCatalogSync.Apply(db);
        }

        using (var db = new AppDbContext(opts))
        {
            Assert.False(db.PermissionDefinitions.Single(p => p.Key == "legacy.thing.gone").Active);
            Assert.True(db.PermissionDefinitions.Single(p => p.Key == Permissions.FleetGatewayView.Key).Active);
        }
    }

    [Fact]
    public void Apply_Round_Trips_Enum_And_Step_Up_Metadata()
    {
        var opts = NewDb();
        using (var db = new AppDbContext(opts))
        {
            PermissionCatalogSync.Apply(db);
        }

        using var read = new AppDbContext(opts);
        var decommission = read.PermissionDefinitions.Single(p => p.Key == Permissions.FleetGatewayDecommission.Key);
        Assert.Equal("High", decommission.Risk);
        Assert.Equal("ManageFleet", decommission.Capability);
        Assert.True(decommission.RequiresFreshAuth);

        var view = read.PermissionDefinitions.Single(p => p.Key == Permissions.FleetGatewayView.Key);
        Assert.Equal("Low", view.Risk);
        Assert.False(view.RequiresFreshAuth);
    }
}
