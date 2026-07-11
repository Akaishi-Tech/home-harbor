using System.Reflection;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class SensitiveControllerAuthorizationTests
{
    [TestMethod]
    public void Sensitive_Mutations_Require_Family_Admin()
    {
        (Type Controller, string Action)[] actions =
        [
            (typeof(AppsController), nameof(AppsController.Install)),
            (typeof(AppsController), nameof(AppsController.Delete)),
            (typeof(AppsController), nameof(AppsController.SetState)),
            (typeof(BackupsController), nameof(BackupsController.CreateTarget)),
            (typeof(BackupsController), nameof(BackupsController.VerifyTarget)),
            (typeof(BackupsController), nameof(BackupsController.Run)),
            (typeof(BackupsController), nameof(BackupsController.OneClick)),
            (typeof(ContainersController), nameof(ContainersController.Create)),
            (typeof(ContainersController), nameof(ContainersController.Update)),
            (typeof(ContainersController), nameof(ContainersController.Start)),
            (typeof(ContainersController), nameof(ContainersController.Stop)),
            (typeof(ContainersController), nameof(ContainersController.Restart)),
            (typeof(ContainersController), nameof(ContainersController.Delete)),
            (typeof(MediaController), nameof(MediaController.Index)),
            (typeof(NetworkingController), nameof(NetworkingController.CreateSelfSigned)),
            (typeof(NetworkingController), nameof(NetworkingController.AddRoute)),
            (typeof(OtaController), nameof(OtaController.Apply)),
            (typeof(OtaController), nameof(OtaController.Stage)),
            (typeof(RecoveryController), nameof(RecoveryController.Start)),
            (typeof(RemoteAccessController), nameof(RemoteAccessController.Create)),
            (typeof(SmbController), nameof(SmbController.CreateShare)),
            (typeof(SmbController), nameof(SmbController.UpdateShare)),
            (typeof(SmbController), nameof(SmbController.CreateCredential)),
            (typeof(SmbController), nameof(SmbController.RevokeCredential))
        ];

        foreach (var (controller, action) in actions)
            AssertPolicy(controller, action, AuthorizationPolicies.FamilyAdmin);
    }

    [TestMethod]
    public void Sync_Upsert_Explicitly_Allows_Device_Basic_And_Admin_Bearer_Authentication()
    {
        var method = typeof(SyncController).GetMethod(nameof(SyncController.Upsert));
        Assert.IsNotNull(method);
        var schemes = method.GetCustomAttributes<AuthorizeAttribute>()
            .Select(attribute => attribute.AuthenticationSchemes)
            .Single(value => !string.IsNullOrWhiteSpace(value));
        Assert.Contains(BasicAuthenticationHandler.SchemeName, schemes!);
        Assert.Contains("Bearer", schemes!);
    }

    [TestMethod]
    public void Sensitive_Metadata_Requires_Admin_Or_Content_Member()
    {
        AssertPolicy(typeof(DevicesController), nameof(DevicesController.List), AuthorizationPolicies.FamilyAdmin);
        AssertPolicy(typeof(WebDavTokensController), nameof(WebDavTokensController.List), AuthorizationPolicies.FamilyAdmin);
        AssertPolicy(typeof(SmbController), nameof(SmbController.Shares), AuthorizationPolicies.FamilyAdmin);
        AssertPolicy(typeof(SmbController), nameof(SmbController.Credentials), AuthorizationPolicies.FamilyAdmin);
        AssertPolicy(typeof(ContainersController), nameof(ContainersController.List), AuthorizationPolicies.FamilyAdmin);
        AssertPolicy(typeof(ContainersController), nameof(ContainersController.Get), AuthorizationPolicies.FamilyAdmin);
        AssertPolicy(typeof(RemoteAccessController), nameof(RemoteAccessController.List), AuthorizationPolicies.FamilyAdmin);
        AssertPolicy(typeof(BackupsController), nameof(BackupsController.Targets), AuthorizationPolicies.FamilyMember);
        AssertPolicy(typeof(VaultController), nameof(VaultController.List), AuthorizationPolicies.FamilyAdmin);
    }

    private static void AssertPolicy(Type controller, string action, string policy)
    {
        var method = controller.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Single(candidate => candidate.Name == action);
        var attributes = controller.GetCustomAttributes<AuthorizeAttribute>()
            .Concat(method.GetCustomAttributes<AuthorizeAttribute>());

        Assert.IsTrue(
            attributes.Any(attribute => attribute.Policy == policy),
            $"{controller.Name}.{action} must require {policy}.");
    }
}
