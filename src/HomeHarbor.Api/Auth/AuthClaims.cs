namespace HomeHarbor.Api.Auth;

public static class AuthClaims
{
    public const string FamilyId = "homeharbor.family_id";
    public const string DeviceId = "homeharbor.device_id";
    public const string SessionId = "homeharbor.session_id";
    public const string TokenKind = "homeharbor.token_kind";
    public const string WebDavScope = "homeharbor.webdav_scope";
}

public static class AuthTokenKinds
{
    public const string User = "user";
    public const string Automation = "automation";
}
