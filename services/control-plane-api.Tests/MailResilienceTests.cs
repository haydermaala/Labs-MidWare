using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ControlPlane.Api.Tests;

/// <summary>An email provider that is completely down.</summary>
public sealed class FailingEmailSender : IEmailSender
{
    public Task SendAsync(OutboundEmail email, CancellationToken ct = default) =>
        throw new InvalidOperationException("smtp unavailable");
}

/// <summary>
/// A degraded mail provider must not change what an unauthenticated caller can
/// observe. Without this, password reset 500s for accounts that exist while
/// unknown addresses still return 202 — an account-enumeration oracle that only
/// appears when mail is broken.
/// </summary>
public sealed class MailResilienceTests
{
    private sealed class BrokenMailFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, cfg) =>
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ControlPlane:AdminToken"] = "test-admin",
                    ["ControlPlane:LoginRatePermit"] = "1000",
                }));
            builder.ConfigureTestServices(s => s.AddSingleton<IEmailSender, FailingEmailSender>());
        }
    }

    private sealed record TenantDto(string Id);
    private sealed record UserDto(string Id);
    private sealed record InvitationDto(string Id, string Email, string Role, string Status);
    private sealed record InviteResponseDto(InvitationDto Invitation, bool EmailDelivered);

    [Fact]
    public async Task Forgot_Password_Cannot_Be_Used_To_Enumerate_Accounts_When_Mail_Is_Down()
    {
        using var factory = new BrokenMailFactory();
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");
        await admin.PostAsJsonAsync("/api/admin/users",
            new { email = "real@example.test", password = "correct horse battery staple" });

        // An account that exists (send is attempted and fails) …
        var existing = await factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password",
            new { email = "real@example.test" });
        // … and one that does not (no send attempted at all).
        var unknown = await factory.CreateClient().PostAsJsonAsync("/api/auth/forgot-password",
            new { email = "ghost@example.test" });

        Assert.Equal(HttpStatusCode.Accepted, existing.StatusCode);
        Assert.Equal(unknown.StatusCode, existing.StatusCode);
    }

    [Fact]
    public async Task Invitation_Survives_A_Mail_Outage_And_Reports_Non_Delivery()
    {
        using var factory = new BrokenMailFactory();
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");

        var tenant = (await (await admin.PostAsJsonAsync("/api/tenants", new { name = "Outage Lab" }))
            .Content.ReadFromJsonAsync<TenantDto>())!;

        var created = await admin.PostAsJsonAsync($"/api/tenants/{tenant.Id}/invitations",
            new { email = "invitee@example.test", role = "technician" });

        // Created, not 500 — the admin is never left unsure whether it exists.
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var body = (await created.Content.ReadFromJsonAsync<InviteResponseDto>())!;
        Assert.False(body.EmailDelivered);
        Assert.Equal("pending", body.Invitation.Status);

        // And it is genuinely durable, so it can be revoked or re-sent.
        var listed = await admin.GetFromJsonAsync<List<InvitationDto>>($"/api/tenants/{tenant.Id}/invitations");
        Assert.Contains(listed!, i => i.Email == "invitee@example.test" && i.Status == "pending");
    }

    [Fact]
    public async Task A_Working_Provider_Reports_Delivery()
    {
        using var factory = new EmailApiFactory(); // capturing sender that succeeds
        var admin = factory.CreateClient();
        admin.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "test-admin");

        var tenant = (await (await admin.PostAsJsonAsync("/api/tenants", new { name = "Delivery Lab" }))
            .Content.ReadFromJsonAsync<TenantDto>())!;
        var created = await admin.PostAsJsonAsync($"/api/tenants/{tenant.Id}/invitations",
            new { email = "delivered@example.test", role = "auditor" });

        var body = (await created.Content.ReadFromJsonAsync<InviteResponseDto>())!;
        Assert.True(body.EmailDelivered);
    }
}
