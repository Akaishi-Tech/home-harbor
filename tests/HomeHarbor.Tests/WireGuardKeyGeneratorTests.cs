using HomeHarbor.Api.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class WireGuardKeyGeneratorTests
{
    [TestMethod]
    public void DerivePublicKey_Matches_Rfc7748_X25519_Vector()
    {
        var privateKey = Convert.FromHexString("77076d0a7318a57d3c16c17251b26645df4c2f87ebc0992ab177fba51db92c2a");
        var expectedPublicKey = Convert.FromHexString("8520f0098930a754748b7ddcb43ef75a0dbf3a0d26381af4eba4a98eaa9b4e6a");

        var publicKey = WireGuardKeyDerivation.DerivePublicKey(privateKey);

        CollectionAssert.AreEqual(expectedPublicKey, publicKey);
    }

    [TestMethod]
    public void GenerateFallbackKeyPair_Returns_Clamped_PrivateKey_With_Derived_PublicKey()
    {
        var pair = WireGuardKeyDerivation.GenerateFallbackKeyPair();

        var privateKey = Convert.FromBase64String(pair.PrivateKey);
        var publicKey = Convert.FromBase64String(pair.PublicKey);

        Assert.AreEqual("managed-fallback", pair.Mode);
        Assert.HasCount(32, privateKey);
        Assert.HasCount(32, publicKey);
        Assert.AreEqual(0, privateKey[0] & 7);
        Assert.AreEqual(0, privateKey[31] & 128);
        Assert.AreEqual(64, privateKey[31] & 64);
        CollectionAssert.AreEqual(publicKey, WireGuardKeyDerivation.DerivePublicKey(privateKey));
    }

    [TestMethod]
    public async Task GenerateAsync_Uses_Derived_Fallback_KeyPair_When_WireGuard_Tools_Fail()
    {
        var generator = new WireGuardKeyGenerator(
            NullLogger<WireGuardKeyGenerator>.Instance,
            (_, _, _) => throw new InvalidOperationException("wg failed"));

        var pair = await generator.GenerateAsync(CancellationToken.None);

        var privateKey = Convert.FromBase64String(pair.PrivateKey);
        var publicKey = Convert.FromBase64String(pair.PublicKey);

        Assert.AreEqual("managed-fallback", pair.Mode);
        CollectionAssert.AreEqual(publicKey, WireGuardKeyDerivation.DerivePublicKey(privateKey));
    }
}
