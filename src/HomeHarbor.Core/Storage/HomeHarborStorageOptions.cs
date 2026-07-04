namespace HomeHarbor.Core.Storage;

public sealed class HomeHarborStorageOptions
{
    public const string SectionName = "HomeHarbor:Storage";

    public string DataRoot { get; set; } = "/homeharbor-data";

    public long MaxUploadBytes { get; set; } = 20L * 1024L * 1024L * 1024L;
}
