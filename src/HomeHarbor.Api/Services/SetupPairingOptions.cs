namespace HomeHarbor.Api.Services;

public sealed class SetupPairingOptions
{
    public const string SectionName = "HomeHarbor:Setup";

    public string BootstrapCodePath { get; set; } = "/var/lib/homeharbor/setup/bootstrap-code";

    public string BootstrapCompletePath { get; set; } = "/var/lib/homeharbor/setup/bootstrap-complete";

    public string ConsumeRequestPath { get; set; } = "/run/homeharbor/setup-bootstrap-consume";
}
