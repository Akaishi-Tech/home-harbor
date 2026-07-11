using HomeHarbor.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class SetupPairingServiceTests
{
    [TestMethod]
    public void BootstrapCode_Is_Read_From_RootProvisioned_File_And_Compared_Case_Insensitively()
    {
        using var fixture = PairingFixture.Create();
        File.WriteAllText(fixture.Options.BootstrapCodePath, "ABCD-EFGH-JKLM-NPQR\n");

        Assert.IsTrue(fixture.Service.IsBootstrapCodeValid("  abcd-efgh-jklm-npqr  "));
        Assert.IsFalse(fixture.Service.IsBootstrapCodeValid("ABCD-EFGH-JKLM-NPQX"));
    }

    [TestMethod]
    public void Missing_BootstrapCode_Fails_Closed()
    {
        using var fixture = PairingFixture.Create();

        Assert.IsFalse(fixture.Service.IsBootstrapCodeValid("ABCD-EFGH-JKLM-NPQR"));
    }

    [TestMethod]
    public void ConsumeBootstrapCode_Invalidates_In_Process_And_Writes_Agent_Request_Contract()
    {
        using var fixture = PairingFixture.Create();
        File.WriteAllText(fixture.Options.BootstrapCodePath, "ABCD-EFGH-JKLM-NPQR");

        fixture.Service.ConsumeBootstrapCode("ABCD-EFGH-JKLM-NPQR");

        Assert.IsFalse(fixture.Service.IsBootstrapCodeValid("ABCD-EFGH-JKLM-NPQR"));
        Assert.AreEqual("consume", File.ReadAllText(fixture.Options.ConsumeRequestPath).Trim());
        Assert.IsTrue(File.Exists(fixture.Options.BootstrapCodePath), "The unprivileged API must not delete the root-owned code directly.");
    }

    [TestMethod]
    public void BootstrapComplete_Uses_RootOwned_Marker()
    {
        using var fixture = PairingFixture.Create();
        Assert.IsFalse(fixture.Service.IsBootstrapComplete());

        File.WriteAllText(fixture.Options.BootstrapCompletePath, "complete\n");

        Assert.IsTrue(fixture.Service.IsBootstrapComplete());
    }

    [TestMethod]
    public void DeviceCode_Is_Single_Use()
    {
        using var fixture = PairingFixture.Create();
        var familyId = Guid.NewGuid();
        var ticket = fixture.Service.GetOrCreate("https://homeharbor.test", familyId);

        Assert.IsTrue(fixture.Service.IsDeviceCodeValid(ticket.Code));
        Assert.IsTrue(fixture.Service.TryConsumeDeviceCode(ticket.Code, out var consumed));
        Assert.AreEqual(familyId, consumed!.FamilyId);
        Assert.IsFalse(fixture.Service.IsDeviceCodeValid(ticket.Code));
        Assert.IsFalse(fixture.Service.TryConsumeDeviceCode(ticket.Code, out _));
    }

    [TestMethod]
    public void DeviceCode_Is_Placed_In_Url_Fragment_Not_Http_Request_Target()
    {
        using var fixture = PairingFixture.Create();

        var ticket = fixture.Service.GetOrCreate("https://homeharbor.test/base/", Guid.NewGuid());
        var uri = new Uri(ticket.PairingUrl);

        Assert.AreEqual("/base/pair", uri.AbsolutePath);
        Assert.AreEqual(string.Empty, uri.Query);
        Assert.AreEqual($"#code={Uri.EscapeDataString(ticket.Code)}", uri.Fragment);
    }

    private sealed class PairingFixture : IDisposable
    {
        private PairingFixture(string root, SetupPairingOptions options, SetupPairingService service)
        {
            Root = root;
            Options = options;
            Service = service;
        }

        public string Root { get; }
        public SetupPairingOptions Options { get; }
        public SetupPairingService Service { get; }

        public static PairingFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), "homeharbor-pairing-" + Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(root);
            var options = new SetupPairingOptions
            {
                BootstrapCodePath = Path.Combine(root, "bootstrap-code"),
                BootstrapCompletePath = Path.Combine(root, "bootstrap-complete"),
                ConsumeRequestPath = Path.Combine(root, "consume-request")
            };
            var service = new SetupPairingService(
                new TokenGenerator(),
                Microsoft.Extensions.Options.Options.Create(options),
                NullLogger<SetupPairingService>.Instance);
            return new PairingFixture(root, options, service);
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
