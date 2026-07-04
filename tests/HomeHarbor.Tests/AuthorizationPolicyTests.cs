using System.Security.Claims;
using HomeHarbor.Api.Auth;
using HomeHarbor.Core.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class AuthorizationPolicyTests
{
    [TestMethod]
    [DataRow(FamilyRoles.Owner)]
    [DataRow(FamilyRoles.Admin)]
    public async Task FamilyAdminPolicy_Allows_Owner_And_Admin(string role)
    {
        var result = await AuthorizeFamilyAdminAsync(User(role));

        Assert.IsTrue(result.Succeeded);
    }

    [TestMethod]
    [DataRow(FamilyRoles.Member)]
    [DataRow(FamilyRoles.Child)]
    [DataRow(FamilyRoles.Guest)]
    public async Task FamilyAdminPolicy_Denies_Non_Admin_Family_Roles(string role)
    {
        var result = await AuthorizeFamilyAdminAsync(User(role));

        Assert.IsFalse(result.Succeeded);
    }

    [TestMethod]
    public async Task FamilyAdminPolicy_Denies_Automation_Tokens()
    {
        var result = await AuthorizeFamilyAdminAsync(
            new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(AuthClaims.TokenKind, AuthTokenKinds.Automation),
                new Claim(ClaimTypes.Role, FamilyRoles.Owner)
            ], "Bearer")));

        Assert.IsFalse(result.Succeeded);
    }

    private static async Task<AuthorizationResult> AuthorizeFamilyAdminAsync(ClaimsPrincipal user)
    {
        using var provider = new ServiceCollection()
            .AddLogging()
            .AddAuthorization(options => options.AddHomeHarborPolicies())
            .BuildServiceProvider();
        var authorization = provider.GetRequiredService<IAuthorizationService>();
        return await authorization.AuthorizeAsync(user, null, AuthorizationPolicies.FamilyAdmin);
    }

    private static ClaimsPrincipal User(string role)
        => new(new ClaimsIdentity(
        [
            new Claim(AuthClaims.TokenKind, AuthTokenKinds.User),
            new Claim(ClaimTypes.Role, role)
        ], "Bearer"));
}
