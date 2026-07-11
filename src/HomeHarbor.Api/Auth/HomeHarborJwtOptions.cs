namespace HomeHarbor.Api.Auth;

public sealed class HomeHarborJwtOptions
{
    public const string SectionName = "HomeHarbor:Jwt";

    public string Issuer { get; set; } = "HomeHarbor";
    public string Audience { get; set; } = "HomeHarbor.Frontend";
    public string SigningKeyPath { get; set; } = "/var/lib/homeharbor/api/jwt-signing.key";
    public int AccessTokenDays { get; set; } = 30;
}
