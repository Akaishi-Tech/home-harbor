using HomeHarbor.Api.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class UnavailableFeatureControllerTests
{
    [TestMethod]
    public void Unimplemented_Privileged_Features_Return_501_Instead_Of_Writing_Fake_Success()
    {
        var backups = new BackupsController(null!, null!);
        var recovery = new RecoveryController(null!, null!);
        var remote = new RemoteAccessController(null!, null!);
        var networking = new NetworkingController(null!, null!, null!, null!);
        var ota = new OtaController();
        var apps = new AppsController(null!, null!, null!, null!, null!);

        AssertNotImplemented(backups.VerifyTarget(Guid.NewGuid()));
        AssertNotImplemented(backups.Run(new BackupsController.RunBackupRequest(Guid.NewGuid())));
        AssertNotImplemented(backups.OneClick(new BackupsController.OneClickBackupRequest(null, null, null, null)));
        AssertNotImplemented(recovery.Start(new RecoveryController.StartRecoveryDrillRequest(null, null)));
        AssertNotImplemented(remote.Create(new RemoteAccessController.CreatePeerRequest(null, null, null)));
        AssertNotImplemented(networking.CreateSelfSigned(new NetworkingController.CreateCertificateRequest(null, "example.test", 30)));
        AssertNotImplemented(ota.Stage(null!));
        AssertNotImplemented(ota.Apply(null!));
        AssertNotImplemented(AppsController.SystemAppOperationUnavailable());
        AssertNotImplemented(apps.SetState(
            Guid.NewGuid(),
            new AppsController.SetAppStateRequest("running"),
            CancellationToken.None));
    }

    private static void AssertNotImplemented(IActionResult result)
    {
        var response = Assert.IsInstanceOfType<ObjectResult>(result);
        Assert.AreEqual(StatusCodes.Status501NotImplemented, response.StatusCode);
    }
}
