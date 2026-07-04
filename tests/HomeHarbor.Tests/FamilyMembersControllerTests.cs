using System.Reflection;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class FamilyMembersControllerTests
{
    [TestMethod]
    [DataRow(nameof(FamilyMembersController.Create))]
    [DataRow(nameof(FamilyMembersController.Delete))]
    public void Mutating_Member_Actions_Require_Family_Admin(string actionName)
    {
        var method = typeof(FamilyMembersController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == actionName);

        var authorize = method
            .GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault(attribute => attribute.Policy == AuthorizationPolicies.FamilyAdmin);

        Assert.IsNotNull(authorize);
    }
}
