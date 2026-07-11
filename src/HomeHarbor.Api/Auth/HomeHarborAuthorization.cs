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
        options.AddPolicy(AuthorizationPolicies.FamilyOwner, UserPolicyBuilder()
            .RequireRole(FamilyRoles.Owner)
            .Build());
        options.AddPolicy(AuthorizationPolicies.FamilyMember, UserPolicyBuilder()
            .RequireRole(FamilyRoles.Owner, FamilyRoles.Admin, FamilyRoles.Member)
            .Build());
    }

    private static AuthorizationPolicyBuilder UserPolicyBuilder()
        => new AuthorizationPolicyBuilder(JwtBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(AuthClaims.TokenKind, AuthTokenKinds.User)
            .RequireClaim(AuthClaims.FamilyId)
            .RequireClaim(AuthClaims.SessionId);
}
