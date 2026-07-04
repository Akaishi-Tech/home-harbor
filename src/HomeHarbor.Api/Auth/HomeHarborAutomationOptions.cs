namespace HomeHarbor.Api.Auth;

public sealed class HomeHarborAutomationOptions
{
    public const string SectionName = "HomeHarbor:Automation";

    public string TokenPath { get; set; } = "/run/homeharbor/automation.jwt";
    public int TokenDays { get; set; } = 365;
}
