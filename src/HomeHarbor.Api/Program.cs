using System.IdentityModel.Tokens.Jwt;
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
                if (!Guid.TryParse(sessionIdRaw, out var sessionId) || string.IsNullOrWhiteSpace(tokenId))
                {
                    context.Fail("Missing session identity.");
                    return;
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<HomeHarborDbContext>();
                var tokenHash = JwtTokenService.HashTokenId(tokenId);
                var session = await db.MemberSessions.AsNoTracking().FirstOrDefaultAsync(
                    s => s.Id == sessionId && s.TokenHash == tokenHash,
                    context.HttpContext.RequestAborted);
                if (session is null || session.ExpiresAt <= DateTimeOffset.UtcNow)
                {
                    context.Fail("Session has expired.");
                    return;
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
        .Get<string[]>() ?? ["http://localhost:5173", "http://127.0.0.1:5173"];
    options.AddPolicy("HomeHarborFrontend", policy =>
        policy.WithOrigins(origins)
            .AllowAnyHeader()
            .AllowAnyMethod());
});
builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddSingleton<IContentTypeProvider, FileExtensionContentTypeProvider>();
builder.Services.AddSingleton<ITokenGenerator, TokenGenerator>();
builder.Services.AddSingleton<IJwtTokenService, JwtTokenService>();
builder.Services.AddSingleton<ISetupPairingService, SetupPairingService>();
builder.Services.AddSingleton<IHomeHarborStorageService, HomeHarborStorageService>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<IFamilyResolver, FamilyResolver>();
builder.Services.AddScoped<IMediaIndexer, MediaIndexer>();
builder.Services.AddSingleton<ICertificateService, CertificateService>();
builder.Services.AddSingleton<IReverseProxyConfigService, ReverseProxyConfigService>();
builder.Services.AddSingleton<IStorageHealthService, StorageHealthService>();
builder.Services.AddSingleton<IStorageOobeService, StorageOobeService>();
builder.Services.AddHttpClient<IAppRuntimeCatalog, AppRuntimeCatalog>(client => client.Timeout = TimeSpan.FromSeconds(5));
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

app.UseDefaultFiles();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (InvalidOperationException ex) when (ex.Message.Contains("Path", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsJsonAsync(new { error = ex.Message });
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
    if (string.IsNullOrWhiteSpace(socketPath)) return;

    _ = builder.WebHost.ConfigureKestrel(options =>
    {
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
