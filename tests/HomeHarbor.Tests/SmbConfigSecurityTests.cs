using HomeHarbor.Api.Services;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class SmbConfigSecurityTests
{
    [TestMethod]
    public void BuildSmbConf_Requires_Modern_Encrypted_Signed_Smb()
    {
        var config = new SmbConfigService().BuildSmbConf([], []);

        Assert.Contains("server min protocol = SMB3_00", config);
        Assert.Contains("server signing = mandatory", config);
        Assert.Contains("smb encrypt = required", config);
        Assert.Contains("ntlm auth = ntlmv2-only", config);
        Assert.Contains("follow symlinks = no", config);
        Assert.Contains("wide links = no", config);
    }
}
