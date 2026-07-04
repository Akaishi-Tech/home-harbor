namespace HomeHarbor.Api.Services;

public sealed class StorageOobeOptions
{
    public const string SectionName = "HomeHarbor:StorageOobe";

    public string StateDirectory { get; set; } = "/var/lib/homeharbor/storage";

    public string OneShotPassphrasePath { get; set; } = "/run/homeharbor/storage-apply.passphrase";

    public string RequestPath { get; set; } = "/run/homeharbor/storage-apply.request";

    public string[] ProtectedPartitionLabels { get; set; } =
    [
        "esp",
        "boot_a",
        "boot_b",
        "super",
        "state",
        "recovery_a",
        "recovery_b",
        "vbmeta_a",
        "vbmeta_b",
        "data",
        "data-candidate"
    ];

    public long MinimumInstallableBytes { get; set; } = 32L * 1024L * 1024L * 1024L;
}
