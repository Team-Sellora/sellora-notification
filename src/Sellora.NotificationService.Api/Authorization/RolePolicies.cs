using Microsoft.AspNetCore.Authorization;

namespace Sellora.NotificationService.Api.Authorization;

// Copied from sellora-organization's RolePolicies.cs (namespace changed).
// This service is event-driven, so only the company admin reads it for now
// (US-E5-3 adds the failed-notification list).
public static class RolePolicies
{
    public const string RequireCompanyAdmin = "RequireCompanyAdmin";
    public const string RequireAreaManager = "RequireAreaManager";
    public const string RequireAgencyOperator = "RequireAgencyOperator";
    public const string RequireSalesRep = "RequireSalesRep";
    public const string RequireShopOwner = "RequireShopOwner";

    public static void AddSelloraRolePolicies(this AuthorizationOptions options)
    {
        options.AddPolicy(RequireCompanyAdmin, p => p.RequireAssertion(HasRole("CompanyAdmin")));
        options.AddPolicy(RequireAreaManager, p => p.RequireAssertion(HasRole("AreaManager")));
        options.AddPolicy(RequireAgencyOperator, p => p.RequireAssertion(HasRole("AgencyOperator")));
        options.AddPolicy(RequireSalesRep, p => p.RequireAssertion(HasRole("SalesRep")));
        options.AddPolicy(RequireShopOwner, p => p.RequireAssertion(HasRole("ShopOwner")));
    }

    // WSO2 IS emits the "roles" claim (plural, an array). Program.cs sets
    // RoleClaimType = "roles" and MapInboundClaims = false, so "roles" arrives
    // unmapped — match on "roles" directly, not ClaimTypes.Role.
    private static Func<AuthorizationHandlerContext, bool> HasRole(string role) =>
        ctx => ctx.User.HasClaim("roles", role);
}
