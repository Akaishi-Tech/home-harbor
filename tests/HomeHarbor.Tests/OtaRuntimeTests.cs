using HomeHarbor.Api.Services;

namespace HomeHarbor.Tests;

[TestClass]
public sealed class OtaRuntimeTests
{
    [TestMethod]
    public void Channel_Uses_Channel_File_Before_Image_Default()
    {
        var previousFile = Environment.GetEnvironmentVariable("HOMEHARBOR_OTA_CHANNEL_FILE");
        var previousChannel = Environment.GetEnvironmentVariable("HOMEHARBOR_CHANNEL");
        var temp = Path.Combine(Path.GetTempPath(), "homeharbor-channel-" + Guid.NewGuid().ToString("N"));
        try
        {
            File.WriteAllText(temp, "stable\n");
            Environment.SetEnvironmentVariable("HOMEHARBOR_OTA_CHANNEL_FILE", temp);
            Environment.SetEnvironmentVariable("HOMEHARBOR_CHANNEL", "daily");

            Assert.AreEqual("stable", OtaRuntime.Channel());
        }
        finally
        {
            Environment.SetEnvironmentVariable("HOMEHARBOR_OTA_CHANNEL_FILE", previousFile);
            Environment.SetEnvironmentVariable("HOMEHARBOR_CHANNEL", previousChannel);
            File.Delete(temp);
        }
    }
}
