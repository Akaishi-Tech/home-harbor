namespace HomeHarbor.Api.Services;

using HomeHarbor.Tooling;

public static class OtaRuntime
{
    public static string Version()
        => Environment.GetEnvironmentVariable("HOMEHARBOR_VERSION") ?? "0.1.0-dev";

    public static IReadOnlyList<string> AvailableChannels()
        => ReleaseChannel.All;

    public static IReadOnlyList<string> AvailableKernelChannels()
        => KernelChannel.All;

    public static string Channel()
    {
        var channelFile = ChannelFilePath();
        if (File.Exists(channelFile))
        {
            var channel = File.ReadLines(channelFile).FirstOrDefault();
            return ReleaseChannel.Require(channel, "OTA channel file");
        }

        return ReleaseChannel.Require(Environment.GetEnvironmentVariable("HOMEHARBOR_CHANNEL") ?? ReleaseChannel.Dev, "HOMEHARBOR_CHANNEL");
    }

    public static string KernelChannelName()
    {
        var channelFile = KernelChannelFilePath();
        if (File.Exists(channelFile))
        {
            var channel = File.ReadLines(channelFile).FirstOrDefault();
            return KernelChannel.Require(channel, "kernel channel file");
        }

        return KernelChannel.Require(Environment.GetEnvironmentVariable("HOMEHARBOR_KERNEL_CHANNEL") ?? KernelChannel.Generic, "HOMEHARBOR_KERNEL_CHANNEL");
    }

    public static string ChannelFilePath()
        => Environment.GetEnvironmentVariable("HOMEHARBOR_OTA_CHANNEL_FILE") ?? "/var/lib/homeharbor/ota/channel";

    public static string KernelChannelFilePath()
        => Environment.GetEnvironmentVariable("HOMEHARBOR_KERNEL_CHANNEL_FILE") ?? "/var/lib/homeharbor/ota/kernel-channel";

    public static string UpdateState()
        => File.Exists(Environment.GetEnvironmentVariable("HOMEHARBOR_OTA_PENDING") ?? "/var/lib/homeharbor/ota/pending.json")
            ? "pending-reboot"
            : "idle";
}
