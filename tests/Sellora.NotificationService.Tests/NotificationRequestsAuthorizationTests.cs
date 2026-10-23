using System.Reflection;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Sellora.NotificationService.Api.Authorization;
using Sellora.NotificationService.Api.Controllers;

namespace Sellora.NotificationService.Tests;

/// <summary>Only a company admin can read queued notifications (they carry emails).</summary>
public sealed class NotificationRequestsAuthorizationTests
{
    [Theory]
    [InlineData("CompanyAdmin", true)]
    [InlineData("AreaManager", false)]
    [InlineData("AgencyOperator", false)]
    [InlineData("SalesRep", false)]
    [InlineData("ShopOwner", false)]
    public async Task Only_a_company_admin_can_read_notification_requests(string role, bool allowed)
    {
        var authorization = new ServiceCollection()
            .AddLogging()
            .AddAuthorizationCore(options => options.AddSelloraRolePolicies())
            .BuildServiceProvider()
            .GetRequiredService<IAuthorizationService>();

        var policy = typeof(NotificationRequestsController).GetCustomAttribute<AuthorizeAttribute>()!.Policy!;
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("roles", role) }, "test"));

        Assert.Equal(allowed, (await authorization.AuthorizeAsync(user, null, policy)).Succeeded);
    }
}
