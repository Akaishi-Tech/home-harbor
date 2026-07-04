using Microsoft.AspNetCore.Http;

namespace HomeHarbor.Api.Services;

public static class PreStorageRequestGate
{
    public static bool RequiresReadyStorage(PathString path)
    {
        return !path.StartsWithSegments("/api/setup", StringComparison.OrdinalIgnoreCase) && !path.Equals("/api/system/health", StringComparison.OrdinalIgnoreCase) && (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)
            || path.StartsWithSegments("/dav", StringComparison.OrdinalIgnoreCase));
    }
}
