using HomeHarbor.Api.Services;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class PublicOriginPolicyTests
{
    [TestMethod]
    public void Normalize_Uses_Only_Configured_Origin()
    {
        Assert.AreEqual("https://homeharbor.local", PublicOriginPolicy.Normalize("https://homeharbor.local/"));
    }

    [TestMethod]
    [DataRow("javascript:alert(1)")]
    [DataRow("https://user:pass@example.test")]
    [DataRow("https://example.test/path")]
    [DataRow("https://example.test/?redirect=evil")]
    public void Normalize_Rejects_Non_Origin_Values(string value)
    {
        _ = Assert.ThrowsExactly<InvalidOperationException>(() => PublicOriginPolicy.Normalize(value));
    }
}
