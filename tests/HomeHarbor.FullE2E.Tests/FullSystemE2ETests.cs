using HomeHarbor.FullE2E.Tests.Infrastructure;

namespace HomeHarbor.FullE2E.Tests;

[TestClass]
public sealed class FullSystemE2ETests
{
    [TestMethod]
    [TestCategory("FullE2E")]
    [Timeout(90 * 60 * 1000, CooperativeCancellation = true)]
    public async Task Full_Appliance_And_Recovery_E2E_Pass()
    {
        if (!E2EOptions.ShouldRunFullE2E())
        {
            Console.WriteLine(
                "Skipping full appliance E2E because HOMEHARBOR_RUN_FULL_E2E is not set to 1. " +
                "Set HOMEHARBOR_RUN_FULL_E2E=1 and HOMEHARBOR_E2E_ISO to boot the installer ISO.");
            return;
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(90));
        var cancellationToken = cts.Token;
        var options = E2EOptions.FromEnvironment();
        var processes = new ProcessRunner();

        await new E2EPreflight(processes).VerifyAsync(options, cancellationToken);
        var installedDisk = await LibvirtHomeHarborVm.InstallFromIsoAsync(processes, options, cancellationToken);
        try
        {
            await using (var normal = await LibvirtHomeHarborVm.BootNormalAsync(
                             processes,
                             options,
                             installedDisk,
                             deleteDiskOnDispose: false,
                             cancellationToken))
            {
                Assert.IsTrue(File.Exists(normal.ReportPath));
                using var scenario = new HomeHarborApiScenario(
                    normal.CreateTrustedHttpClient,
                    normal.SetupBootstrapCode);
                await scenario.RunAsync(cancellationToken);
            }

            await using var recovery = await LibvirtHomeHarborVm.BootRecoveryAsync(
                             processes,
                             options,
                             installedDisk,
                             deleteDiskOnDispose: !options.KeepVm,
                             cancellationToken);
            installedDisk = string.Empty;
            Assert.IsTrue(File.Exists(recovery.ReportPath));

        }
        finally
        {
            if (!options.KeepVm && !string.IsNullOrWhiteSpace(installedDisk) && File.Exists(installedDisk))
            {
                File.Delete(installedDisk);
            }
        }
    }
}
