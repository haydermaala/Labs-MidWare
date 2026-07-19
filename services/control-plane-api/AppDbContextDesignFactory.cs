// Design-time factory used ONLY by the EF Core tools (`dotnet ef migrations …`).
//
// At design time there is no running host and no DATABASE_URL, so the tools need a
// way to construct the context with the Npgsql provider to emit provider-correct
// migration SQL. The connection string here is a placeholder — migrations are
// generated from the model, not from a live database, so nothing connects to it.
// At runtime the app builds its own AppDbContext from configuration (Program.cs);
// this type is never used there.

using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ControlPlane.Api;

/// <summary>Constructs an AppDbContext for the EF Core command-line tools.</summary>
public sealed class AppDbContextDesignFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Port=5432;Database=labconnect;Username=postgres;Password=postgres")
            .Options;
        return new AppDbContext(options);
    }
}
