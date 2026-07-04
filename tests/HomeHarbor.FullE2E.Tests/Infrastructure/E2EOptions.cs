namespace HomeHarbor.FullE2E.Tests.Infrastructure;

internal sealed record E2EOptions(
    string Version,
    string DiskSize,
    string IsoPath,
    string Connect,
    string DataPassphrase,
    bool KeepVm)
{
    public static E2EOptions FromEnvironment()
    {
        var connect = Environment.GetEnvironmentVariable("HOMEHARBOR_E2E_CONNECT");
        if (string.IsNullOrWhiteSpace(connect))
        {
            connect = Environment.GetEnvironmentVariable("LIBVIRT_DEFAULT_URI");
        }

        var version = Environment.GetEnvironmentVariable("HOMEHARBOR_E2E_VERSION") ?? "0.1.0-e2e";
        var isoPath = Environment.GetEnvironmentVariable("HOMEHARBOR_E2E_ISO");
        if (string.IsNullOrWhiteSpace(isoPath))
        {
            isoPath = Path.Combine(
                RepoPaths.Artifacts,
                "channels",
                version,
                $"homeharbor-full-live-installer-stable-{version}.iso");
        }

        return new E2EOptions(
            version,
            Environment.GetEnvironmentVariable("HOMEHARBOR_E2E_DISK_SIZE") ?? "64G",
            isoPath,
            string.IsNullOrWhiteSpace(connect) ? "qemu:///system" : connect,
            Environment.GetEnvironmentVariable("HOMEHARBOR_E2E_DATA_PASSPHRASE") ?? "homeharbor-e2e-passphrase",
            IsTruthy(Environment.GetEnvironmentVariable("HOMEHARBOR_E2E_KEEP_VM")));
    }

    public static bool ShouldRunFullE2E()
        => string.Equals(Environment.GetEnvironmentVariable("HOMEHARBOR_RUN_FULL_E2E"), "1", StringComparison.Ordinal);

    private static bool IsTruthy(string? value)
        => string.Equals(value, "1", StringComparison.Ordinal) ||
            string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
