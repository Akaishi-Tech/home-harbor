namespace HomeHarbor.Api.Services;

public static class PublicOriginPolicy
{
    public static string Normalize(string? configuredOrigin)
    {
        if (!Uri.TryCreate(configuredOrigin, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
        {
            throw new InvalidOperationException(
                "HomeHarbor:Api:PublicOrigin must be an absolute HTTP(S) origin without credentials, a path, query, or fragment.");
        }

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
