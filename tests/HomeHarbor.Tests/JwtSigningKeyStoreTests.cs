using HomeHarbor.Api.Auth;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class JwtSigningKeyStoreTests
{
    [TestMethod]
    public void GetOrCreateKey_Creates_Missing_Key_With_Private_Permissions()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "jwt-signing.key");

            var key = JwtSigningKeyStore.GetOrCreateKey(path);

            Assert.AreEqual(64, key.Length);
            CollectionAssert.AreEqual(key, Convert.FromBase64String(File.ReadAllText(path)));
            if (!OperatingSystem.IsWindows())
            {
                Assert.AreEqual(
                    UnixFileMode.UserRead | UnixFileMode.UserWrite,
                    File.GetUnixFileMode(path));
            }
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void GetOrCreateKey_Returns_Existing_Key()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "jwt-signing.key");
            var expected = CreateKey(32);
            var encoded = Convert.ToBase64String(expected);
            File.WriteAllText(path, encoded);

            var actual = JwtSigningKeyStore.GetOrCreateKey(path);

            CollectionAssert.AreEqual(expected, actual);
            Assert.AreEqual(encoded, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void GetOrCreateKey_Rejects_Corrupt_Existing_Key_Without_Replacing_It()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "jwt-signing.key");
            File.WriteAllText(path, "not base64");

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                JwtSigningKeyStore.GetOrCreateKey(path));

            StringAssert.Contains(ex.Message, "base64");
            Assert.IsInstanceOfType<FormatException>(ex.InnerException);
            Assert.AreEqual("not base64", File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public void GetOrCreateKey_Rejects_Short_Existing_Key_Without_Replacing_It()
    {
        var tempDir = CreateTempDirectory();
        try
        {
            var path = Path.Combine(tempDir, "jwt-signing.key");
            var encoded = Convert.ToBase64String(CreateKey(31));
            File.WriteAllText(path, encoded);

            var ex = Assert.ThrowsExactly<InvalidOperationException>(() =>
                JwtSigningKeyStore.GetOrCreateKey(path));

            StringAssert.Contains(ex.Message, "at least 32 bytes");
            Assert.AreEqual(encoded, File.ReadAllText(path));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "homeharbor-jwt-key-" + Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(path);
        return path;
    }

    private static byte[] CreateKey(int length)
        => Enumerable.Range(0, length).Select(value => (byte)value).ToArray();
}
