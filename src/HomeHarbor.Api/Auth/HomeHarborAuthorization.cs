using HomeHarbor.Core.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;

namespace HomeHarbor.Api.Auth;

public static class HomeHarborAuthorization
{
    public static void AddHomeHarborPolicies(this AuthorizationOptions options)
    {
        var userPolicy = UserPolicyBuilder().Build();
        options.FallbackPolicy = userPolicy;
        options.AddPolicy(AuthorizationPolicies.Automation, new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(AuthClaims.TokenKind, AuthTokenKinds.Automation)
            .Build());
        options.AddPolicy(AuthorizationPolicies.FamilyAdmin, UserPolicyBuilder()
            .RequireRole(FamilyRoles.Owner, FamilyRoles.Admin)
            .Build());
    }

    private static AuthorizationPolicyBuilder UserPolicyBuilder()
        => new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(AuthClaims.TokenKind, AuthTokenKinds.User);
}
