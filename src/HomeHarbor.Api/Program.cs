using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text.Json.Serialization;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Storage;
using HomeHarbor.Tooling;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var migrateDatabase = args.Length > 0
    && string.Equals(args[0], "database-migrate", StringComparison.Ordinal);

var builder = WebApplication.CreateBuilder(args);

if (!migrateDatabase)
{
    ConfigureApiSocket(builder);
}

builder.Services.Configure<HomeHarborApiOptions>(
    builder.Configuration.GetSection(HomeHarborApiOptions.SectionName));
builder.Services.Configure<HomeHarborJwtOptions>(
    builder.Configuration.GetSection(HomeHarborJwtOptions.SectionName));
builder.Services.Configure<HomeHarborAutomationOptions>(
    builder.Configuration.GetSection(HomeHarborAutomationOptions.SectionName));
builder.Services.Configure<HomeHarborStorageOptions>(
    builder.Configuration.GetSection(HomeHarborStorageOptions.SectionName));
builder.Services.Configure<HomeHarborDatabaseOptions>(
    builder.Configuration.GetSection(HomeHarborDatabaseOptions.SectionName));
builder.Services.Configure<HomeHarborCacheOptions>(
    builder.Configuration.GetSection(HomeHarborCacheOptions.SectionName));
builder.Services.Configure<HomeHarborRuntimeOptions>(
    builder.Configuration.GetSection(HomeHarborRuntimeOptions.SectionName));
builder.Services.Configure<StorageOobeOptions>(
    builder.Configuration.GetSection(StorageOobeOptions.SectionName));
builder.Services.Configure<SetupPairingOptions>(
    builder.Configuration.GetSection(SetupPairingOptions.SectionName));

var cacheOptions = builder.Configuration
    .GetSection(HomeHarborCacheOptions.SectionName)
    .Get<HomeHarborCacheOptions>() ?? new HomeHarborCacheOptions();
var useDevelopmentMemoryCacheFallback = !migrateDatabase &&
    HomeHarborCacheBackend.ShouldUseDevelopmentMemoryFallback(builder.Environment, cacheOptions);
if (useDevelopmentMemoryCacheFallback)
{
    _ = builder.Services.AddDistributedMemoryCache();
}
else
{
    _ = builder.Services.AddStackExchangeRedisCache(options =>
    {
        var configuration = new ConfigurationOptions
        {
            AbortOnConnectFail = true,
            ClientName = "HomeHarbor.Api"
        };
        configuration.EndPoints.Add(new UnixDomainSocketEndPoint(cacheOptions.UnixSocketPath));
        options.ConfigurationOptions = configuration;
        options.InstanceName = cacheOptions.InstanceName;
    });

    if (!migrateDatabase)
    {
        _ = builder.Services.AddHostedService<ValkeyCacheStartupValidator>();
    }
}

builder.Services.AddSingleton<IOverviewCache, OverviewCache>();
builder.Services.AddSingleton<IOverviewCacheInvalidator>(services => services.GetRequiredService<IOverviewCache>());
builder.Services.AddScoped<OverviewCacheInvalidationInterceptor>();

builder.Services.AddDbContext<HomeHarborDbContext>((services, options) =>
{
    var storageOptions = services.GetRequiredService<IOptions<HomeHarborStorageOptions>>().Value;
    var databaseOptions = services.GetRequiredService<IOptions<HomeHarborDatabaseOptions>>().Value;
    _ = Directory.CreateDirectory(storageOptions.DataRoot);
    _ = options.UseNpgsql(databaseOptions.ConnectionString);
    _ = options.AddInterceptors(services.GetRequiredService<OverviewCacheInvalidationInterceptor>());
});

builder.Services
    .AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer()
    .AddScheme<AuthenticationSchemeOptions, BasicAuthenticationHandler>(
        BasicAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<HomeHarborJwtOptions>>((options, jwtOptionsAccessor) =>
    {
        var jwtOptions = jwtOptionsAccessor.Value;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtOptions.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtOptions.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtSigningKeyStore.GetOrCreateSecurityKey(jwtOptions.SigningKeyPath),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role
        };
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var principal = context.Principal;
                var tokenKind = principal?.FindFirstValue(AuthClaims.TokenKind);
                if (string.Equals(tokenKind, AuthTokenKinds.Automation, StringComparison.Ordinal))
                    return;

                if (!string.Equals(tokenKind, AuthTokenKinds.User, StringComparison.Ordinal))
                {
                    context.Fail("Unsupported token kind.");
                    return;
                }

                var sessionIdRaw = principal?.FindFirstValue(AuthClaims.SessionId);
                var tokenId = principal?.FindFirstValue(JwtRegisteredClaimNames.Jti);
                var familyIdRaw = principal?.FindFirstValue(AuthClaims.FamilyId);
                var memberIdRaw = principal?.FindFirstValue(JwtRegisteredClaimNames.Sub)
                    ?? principal?.FindFirstValue(ClaimTypes.NameIdentifier);
                if (!Guid.TryParse(sessionIdRaw, out var sessionId) ||
                    !Guid.TryParse(familyIdRaw, out var familyId) ||
                    !Guid.TryParse(memberIdRaw, out var memberId) ||
                    string.IsNullOrWhiteSpace(tokenId))
                {
                    context.Fail("Missing session identity.");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<HomeHarborDbContext>();
                var tokenHash = JwtTokenService.HashTokenId(tokenId);
                var session = await db.MemberSessions.AsNoTracking().FirstOrDefaultAsync(
                    s => s.Id == sessionId &&
                        s.TokenHash == tokenHash &&
                        s.FamilyId == familyId &&
                        s.MemberId == memberId,
                    context.HttpContext.RequestAborted);
                if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    context.Fail("Session has expired.");
                    return;
                }

                var member = await db.FamilyMembers.AsNoTracking().FirstOrDefaultAsync(
                    m => m.Id == memberId && m.FamilyId == familyId,
                    context.HttpContext.RequestAborted);
                var claimedRole = principal?.FindFirstValue(ClaimTypes.Role);
                if (member is null || !string.Equals(member.Role, claimedRole, StringComparison.Ordinal))
                {
                    context.Fail("Session membership has changed. Sign in again.");
                }
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddHomeHarborPolicies();
});
builder.Services.AddCors(options =>
{
    var origins = builder.Configuration
        .GetSection("HomeHarbor:Frontend:AllowedOrigins")
        .Get<string[]>() ?? [];
    options.AddPolicy("HomeHarborFrontend", policy =>
    {
        if (origins.Length > 0) _ = policy.WithOrigins(origins);
        _ = policy.AllowAnyHeader().AllowAnyMethod();
    });
});
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<IContentTypeProvider, FileExtensionContentTypeProvider>();
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
builder.Services.AddSingleton<AuthenticationFailureThrottle>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<ISetupPairingService, SetupPairingService>();
builder.Services.AddSingleton<IHomeHarborStorageService, HomeHarborStorageService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IFamilyResolver, FamilyResolver>();
builder.Services.AddScoped<IMediaIndexer, MediaIndexer>();
builder.Services.AddSingleton<IReverseProxyConfigService, ReverseProxyConfigService>();
builder.Services.AddSingleton<IStorageHealthService, StorageHealthService>();
builder.Services.AddSingleton<IStorageOobeService, StorageOobeService>();
builder.Services
    .AddHttpClient<IAppRuntimeCatalog, AppRuntimeCatalog>(client => client.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(5)
    });
builder.Services.AddSingleton<KernelModuleDetector>();
builder.Services.AddSingleton<ISmbConfigService, SmbConfigService>();
builder.Services.AddSingleton<IRuntimeSignalService, RuntimeSignalService>();
builder.Services.AddSingleton<IManagedContainerSpecService, ManagedContainerSpecService>();
builder.Services.AddSingleton<IWireGuardKeyGenerator, WireGuardKeyGenerator>();
builder.Services.AddOpenApi();

if (migrateDatabase)
{
    await using var migrationApp = builder.Build();
    using var scope = migrationApp.Services.CreateScope();
    await RunDatabaseMigrationAsync(scope.ServiceProvider, CancellationToken.None);
    return;
}

var app = builder.Build();
const long maxApiRequestBodyBytes = 1024 * 1024;

if (useDevelopmentMemoryCacheFallback)
{
    app.Logger.LogWarning(
        "Valkey Unix socket {UnixSocketPath} is unavailable in Development; using in-memory distributed cache fallback.",
        cacheOptions.UnixSocketPath);
}

using (var scope = app.Services.CreateScope())
{
    var storageOptions = scope.ServiceProvider.GetRequiredService<IOptions<HomeHarborStorageOptions>>().Value;
    _ = Directory.CreateDirectory(storageOptions.DataRoot);
}

if (app.Environment.IsDevelopment())
{
    _ = app.MapOpenApi();
}

app.Use(async (context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.XFrameOptions = "DENY";
    context.Response.Headers.ContentSecurityPolicy =
        "default-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'; form-action 'self'; " +
        "script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; font-src 'self' data:; " +
        "connect-src 'self'; media-src 'self' blob:; worker-src 'self' blob:; manifest-src 'self'";
    context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=(), usb=()";
    var trustedForwardedHttps = context.Connection.RemoteIpAddress is null &&
        string.Equals(
            context.Request.Headers["X-Forwarded-Proto"].ToString().Split(',')[0].Trim(),
            Uri.UriSchemeHttps,
            StringComparison.OrdinalIgnoreCase);
    if (context.Request.IsHttps || trustedForwardedHttps)
        context.Response.Headers.StrictTransportSecurity = "max-age=31536000";

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        var maxBodyFeature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (maxBodyFeature is { IsReadOnly: false }) maxBodyFeature.MaxRequestBodySize = maxApiRequestBodyBytes;
        if (context.Request.ContentLength is > maxApiRequestBodyBytes)
        {
            context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
            await context.Response.WriteAsJsonAsync(new { error = "API request body exceeds the 1 MiB limit." });
            return;
        }
    }

    if (context.Request.Path.StartsWithSegments("/api"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
    }

    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (InvalidOperationException ex)
    {
        if (context.Response.HasStarted) throw;
        app.Logger.LogWarning(ex, "Rejected invalid request {Method} {Path}; trace {TraceId}.", context.Request.Method, context.Request.Path, context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "The request is invalid.", traceId = context.TraceIdentifier });
    }
    catch (UnauthorizedAccessException ex)
    {
        if (context.Response.HasStarted) throw;
        app.Logger.LogWarning(ex, "Denied request {Method} {Path}; trace {TraceId}.", context.Request.Method, context.Request.Path, context.TraceIdentifier);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = "Access is denied.", traceId = context.TraceIdentifier });
    }
});
app.UseCors("HomeHarborFrontend");
app.Use(async (context, next) =>
{
    if (PreStorageRequestGate.RequiresReadyStorage(context.Request.Path))
    {
        var storageOobe = context.RequestServices.GetRequiredService<IStorageOobeService>();
        if (!await storageOobe.IsReadyAsync(context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
            await context.Response.WriteAsJsonAsync(new
            {
                error = "Complete Web OOBE storage apply before using the database-backed API.",
                setup = "/api/setup"
            });
            return;
        }
    }

    await next();
});
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapFallbackToFile("index.html").AllowAnonymous();

app.Run();

static void ConfigureApiSocket(WebApplicationBuilder builder)
{
    var socketPath = builder.Configuration.GetValue<string>($"{HomeHarborApiOptions.SectionName}:UnixSocketPath");
    var maxUploadBytes = builder.Configuration.GetValue<long?>(
        $"{HomeHarborStorageOptions.SectionName}:MaxUploadBytes") ?? new HomeHarborStorageOptions().MaxUploadBytes;

    _ = builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.MaxRequestBodySize = Math.Max(1, maxUploadBytes);
        if (string.IsNullOrWhiteSpace(socketPath)) return;

        var directory = Path.GetDirectoryName(socketPath);
        if (!string.IsNullOrWhiteSpace(directory)) _ = Directory.CreateDirectory(directory);
        File.Delete(socketPath);
        options.ListenUnixSocket(socketPath);
    });
}

static async Task RunDatabaseMigrationAsync(IServiceProvider services, CancellationToken cancellationToken)
{
    var db = services.GetRequiredService<HomeHarborDbContext>();
    await db.Database.MigrateAsync(cancellationToken);
    var jwtTokens = services.GetRequiredService<IJwtTokenService>();
    await jwtTokens.WriteAutomationTokenAsync(cancellationToken);
}
