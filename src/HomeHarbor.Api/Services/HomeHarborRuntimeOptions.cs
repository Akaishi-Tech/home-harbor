namespace HomeHarbor.Api.Services;

public sealed class HomeHarborRuntimeOptions
{
    public const string SectionName = "HomeHarbor:Runtime";

    public string RequestDirectory { get; set; } = "/run/homeharbor";

    public string SmbCredentialDirectory { get; set; } = "/run/homeharbor-smb-credentials";

    public string DataUnlockMetadataPath { get; set; } = "/var/lib/homeharbor/security/data-unlock.json";
}
