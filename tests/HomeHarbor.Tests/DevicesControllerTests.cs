using System.Reflection;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class DevicesControllerTests
{
    [TestMethod]
    public void Register_Requires_Family_Admin()
    {
        var method = typeof(DevicesController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(DevicesController.Register));

        var authorize = method
            .GetCustomAttributes<AuthorizeAttribute>()
            .SingleOrDefault(attribute => attribute.Policy == AuthorizationPolicies.FamilyAdmin);

        Assert.IsNotNull(authorize);
    }
}
