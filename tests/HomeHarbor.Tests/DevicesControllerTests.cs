using System.Reflection;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class DevicesControllerTests
{
    [TestMethod]
    public void Register_Uses_Explicit_Anonymous_Pairing_Flow()
    {
        var method = typeof(DevicesController)
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(method => method.Name == nameof(DevicesController.Register));

        var allowAnonymous = method.GetCustomAttribute<AllowAnonymousAttribute>();

        Assert.IsNotNull(allowAnonymous);
    }
}
