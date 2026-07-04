using System.Security.Claims;
using System.Text.Json;
using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using HomeHarbor.Core.Identity;
using HomeHarbor.Tooling;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/apps")]
public sealed class AppsController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    IAppRuntimeCatalog catalog,
    IManagedContainerSpecService specs,
    IRuntimeSignalService signals) : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var role = CurrentRole();
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        List<ManagedAppEntity> installRows = resolved is null
            ? []
            : await db.ManagedApps
                .AsNoTracking()
                .Where(a => a.FamilyId == resolved.Value && a.DesiredState != "deleted")
                .ToListAsync(cancellationToken);
        var installs = installRows.ToDictionary(a => a.AppKey, StringComparer.OrdinalIgnoreCase);

        var templates = await catalog.ListAsync(role, cancellationToken);
        return Ok(templates.Select(template =>
        {
            _ = installs.TryGetValue(template.AppKey, out var app);
            return new
            {
                template.AppKey,
                template.DisplayName,
                template.Title,
                template.Description,
                template.Category,
                template.Kind,
                template.InstallMode,
                template.Image,
                template.Port,
                template.Version,
                template.ManifestUrl,
                template.RecommendedInSetup,
                template.RequiresReboot,
                template.Available,
                template.UnavailableReason,
                template.Source,
                template.Commands,
                installed = app is not null,
                installId = app?.Id,
                desiredState = app?.DesiredState,
                runtimeState = app?.RuntimeState ?? app?.State,
                installedVersion = app?.InstalledVersion,
                activeVersion = app?.ActiveVersion,
                appRequiresReboot = app?.RequiresReboot ?? false,
                lastError = app?.LastError ?? string.Empty,
                lastAppliedAt = app?.LastAppliedAt
            };
        }));
    }

    [HttpGet("installs")]
    public async Task<IActionResult> Installs([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var role = CurrentRole();
        var canSeeSystemApps =
            string.Equals(role, FamilyRoles.Owner, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(role, FamilyRoles.Admin, StringComparison.OrdinalIgnoreCase);
        var installs = await db.ManagedApps
            .AsNoTracking()
            .Where(a => a.FamilyId == resolved.Value &&
                        a.DesiredState != "deleted" &&
                        (a.Kind != "system" || canSeeSystemApps))
            .OrderBy(a => a.DisplayName)
            .ToListAsync(cancellationToken);
        return Ok(installs.Select(AppResponse));
    }

    [HttpPost("installs")]
    public async Task<IActionResult> Install([FromBody] InstallAppRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });
        var template = await catalog.FindAsync(request.AppKey, cancellationToken);
        if (template is null || !template.IsVisibleTo(CurrentRole())) return NotFound(new { error = "Unknown app." });
        if (!template.Available)
        {
            var error = string.IsNullOrWhiteSpace(template.UnavailableReason)
                ? "App is not available on this appliance."
                : template.UnavailableReason;
            return Conflict(new { error });
        }

        return template.Kind == "system"
            ? !CurrentUserIsAdmin() ? Forbid() : await InstallSystemAppAsync(resolved.Value, template, cancellationToken)
            : await InstallContainerAppAsync(resolved.Value, template, cancellationToken);
    }

    [HttpDelete("installs/{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var app = await db.ManagedApps.FindAsync([id], cancellationToken);
        if (app is null) return NotFound(new { error = "App install not found." });
        await families.RequireAccessAsync(app.FamilyId, cancellationToken);
        if (app.Kind == "system" && !CurrentUserIsAdmin()) return Forbid();

        app.DesiredState = "deleted";
        app.RuntimeState = "pending-delete";
        app.State = app.RuntimeState;
        app.LastError = string.Empty;
        app.UpdatedAt = DateTimeOffset.UtcNow;

        if (app.Kind == "container")
        {
            if (app.ContainerId is { } containerId)
            {
                var container = await db.ManagedContainers.FindAsync([containerId], cancellationToken);
                if (container is not null && container.DeletedAt is null)
                {
                    container.DesiredState = "deleted";
                    container.RequestedAction = "delete";
                    container.RuntimeState = "pending-delete";
                    container.DeletedAt = DateTimeOffset.UtcNow;
                    container.UpdatedAt = DateTimeOffset.UtcNow;
                }
            }

            _ = await db.SaveChangesAsync(cancellationToken);
            signals.RequestContainerApply();
            return Ok(AppResponse(app));
        }

        app.RequiresReboot = true;
        _ = await db.SaveChangesAsync(cancellationToken);
        signals.RequestSystemAppApply();
        return Ok(AppResponse(app));
    }

    [HttpPost("installs/{id:guid}/state")]
    public async Task<IActionResult> SetState(Guid id, [FromBody] SetAppStateRequest request, CancellationToken cancellationToken)
    {
        var app = await db.ManagedApps.FindAsync([id], cancellationToken);
        if (app is null) return NotFound();
        await families.RequireAccessAsync(app.FamilyId, cancellationToken);
        if (app.Kind == "system" && !CurrentUserIsAdmin()) return Forbid();
        app.State = string.IsNullOrWhiteSpace(request.State) ? "planned" : request.State.Trim().ToLowerInvariant();
        app.RuntimeState = app.State;
        app.UpdatedAt = DateTimeOffset.UtcNow;
        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(new { app.Id, app.AppKey, app.State, app.UpdatedAt });
    }

    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpGet("reconcile/desired")]
    public async Task<IActionResult> Desired(CancellationToken cancellationToken)
    {
        var apps = await db.ManagedApps
            .AsNoTracking()
            .Where(a => a.Kind == "system" && (a.DesiredState == "installed" || a.DesiredState == "deleted"))
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(cancellationToken);

        return Ok(apps.Select(app =>
        {
            var manifest = ReadManifest(app);
            return new
            {
                app.Id,
                app.FamilyId,
                app.AppKey,
                app.DisplayName,
                app.Kind,
                app.DesiredState,
                app.RuntimeState,
                app.InstalledVersion,
                app.ActiveVersion,
                app.RequiresReboot,
                app.LastAppliedAt,
                manifestUrl = ManifestUrl(manifest),
                version = JsonString(HhafManifest(manifest), "version"),
                commands = ManifestCommands(app.AppKey, manifest),
                hotCheck = ManifestHotCheck(app.AppKey, manifest)
            };
        }));
    }

    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpPost("reconcile/result")]
    public async Task<IActionResult> ApplyResult([FromBody] AppApplyResultRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in request.Apps ?? [])
        {
            var app = await db.ManagedApps.FindAsync([item.Id], cancellationToken);
            if (app is null) continue;
            app.RuntimeState = string.IsNullOrWhiteSpace(item.RuntimeState) ? app.RuntimeState : item.RuntimeState.Trim();
            app.State = app.RuntimeState;
            if (!string.IsNullOrWhiteSpace(item.DesiredState)) app.DesiredState = item.DesiredState.Trim();
            if (item.InstalledVersion is not null) app.InstalledVersion = item.InstalledVersion;
            if (item.ActiveVersion is not null) app.ActiveVersion = item.ActiveVersion;
            if (item.RequiresReboot is { } requiresReboot) app.RequiresReboot = requiresReboot;
            app.LastError = item.Error ?? string.Empty;
            app.LastAppliedAt = now;
            app.UpdatedAt = now;
        }

        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(new { appliedAt = now });
    }

    private async Task<IActionResult> InstallContainerAppAsync(Guid familyId, ManagedAppTemplate template, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var app = await db.ManagedApps.FirstOrDefaultAsync(
            a => a.FamilyId == familyId && a.AppKey == template.AppKey,
            cancellationToken);
        if (app is null)
        {
            app = new ManagedAppEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = familyId,
                AppKey = template.AppKey,
                CreatedAt = now
            };
            _ = db.ManagedApps.Add(app);
        }

        ManagedContainerEntity? container = null;
        if (app.ContainerId is { } existingContainerId)
        {
            container = await db.ManagedContainers.FindAsync([existingContainerId], cancellationToken);
        }

        if (container is null || container.DeletedAt is not null)
        {
            var containerId = Guid.NewGuid();
            if (template.Manifest.Install is not HomeHarborContainerAppInstall containerInstall)
            {
                return BadRequest(new { error = "App manifest does not describe a container install." });
            }

            var request = new ContainerDefinitionRequest(
                template.DisplayName,
                containerInstall.Image,
                containerInstall.Environment,
                containerInstall.Ports.Select(port => new ContainerPortRequest(port.HostPort, port.ContainerPort, port.Protocol)).ToArray(),
                containerInstall.Volumes.Select(volume => new ContainerVolumeRequest(volume.HostPath, volume.ContainerPath, volume.ReadOnly)).ToArray(),
                containerInstall.Command,
                null,
                null,
                null,
                null,
                null);
            var definition = specs.Normalize(familyId, containerId, request);
            if (await db.ManagedContainers.AsNoTracking().AnyAsync(c => c.FamilyId == familyId && c.Name == definition.Name && c.DeletedAt == null, cancellationToken))
            {
                return Conflict(new { error = "A container with this app name already exists." });
            }

            container = new ManagedContainerEntity
            {
                Id = containerId,
                FamilyId = familyId,
                Name = definition.Name,
                Image = definition.Image,
                DesiredState = "running",
                RuntimeState = "pending",
                RequestedAction = "start",
                ServiceName = $"homeharbor-{containerId:N}",
                DefinitionJson = specs.Serialize(definition),
                CreatedAt = now,
                UpdatedAt = now
            };
            _ = db.ManagedContainers.Add(container);
            app.ContainerId = container.Id;
        }
        else
        {
            container.DesiredState = "running";
            container.RuntimeState = "pending";
            container.RequestedAction = "start";
            container.LastError = string.Empty;
            container.UpdatedAt = now;
        }

        app.Kind = template.Kind;
        app.DisplayName = template.DisplayName;
        app.Image = template.Image;
        app.DesiredState = "installed";
        app.RuntimeState = "pending";
        app.State = app.RuntimeState;
        app.InstalledVersion = template.Version;
        app.ActiveVersion = template.Version;
        app.RequiresReboot = false;
        app.LastError = string.Empty;
        app.ManifestJson = JsonSerializer.Serialize(new
        {
            hhaf = JsonDocument.Parse(template.ManifestJson).RootElement.Clone(),
            containerId = container.Id
        }, JsonOptions);
        app.UpdatedAt = now;

        _ = await db.SaveChangesAsync(cancellationToken);
        signals.RequestContainerApply();
        return Ok(AppResponse(app));
    }

    private async Task<IActionResult> InstallSystemAppAsync(Guid familyId, ManagedAppTemplate template, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var app = await db.ManagedApps.FirstOrDefaultAsync(
            a => a.FamilyId == familyId && a.AppKey == template.AppKey,
            cancellationToken);
        if (app is null)
        {
            app = new ManagedAppEntity
            {
                Id = Guid.NewGuid(),
                FamilyId = familyId,
                AppKey = template.AppKey,
                CreatedAt = now
            };
            _ = db.ManagedApps.Add(app);
        }

        app.Kind = template.Kind;
        app.DisplayName = template.DisplayName;
        app.Image = string.Empty;
        app.DesiredState = "installed";
        app.RuntimeState = "pending-download";
        app.State = app.RuntimeState;
        app.InstalledVersion = string.Empty;
        app.RequiresReboot = true;
        app.LastError = string.Empty;
        app.ManifestJson = JsonSerializer.Serialize(new
        {
            hhaf = JsonDocument.Parse(template.ManifestJson).RootElement.Clone()
        }, JsonOptions);
        app.UpdatedAt = now;

        _ = await db.SaveChangesAsync(cancellationToken);
        signals.RequestSystemAppApply();
        return Ok(AppResponse(app));
    }

    private static object AppResponse(ManagedAppEntity app)
        => new
        {
            app.Id,
            app.FamilyId,
            app.AppKey,
            app.Kind,
            app.DisplayName,
            app.Image,
            app.State,
            app.DesiredState,
            app.RuntimeState,
            app.InstalledVersion,
            app.ActiveVersion,
            app.RequiresReboot,
            app.ContainerId,
            app.LastError,
            app.ManifestJson,
            app.CreatedAt,
            app.UpdatedAt,
            app.LastAppliedAt
        };

    private bool CurrentUserIsAdmin()
    {
        var role = CurrentRole();
        return string.Equals(role, FamilyRoles.Owner, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(role, FamilyRoles.Admin, StringComparison.OrdinalIgnoreCase);
    }

    private string? CurrentRole()
        => User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("role");

    private static JsonElement ReadManifest(ManagedAppEntity app)
        => JsonDocument.Parse(string.IsNullOrWhiteSpace(app.ManifestJson) ? "{}" : app.ManifestJson).RootElement.Clone();

    private static string? JsonString(JsonElement element, string name)
        => element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ValueKind == JsonValueKind.String ? property.GetString() : property.ToString()
            : null;

    private static JsonElement HhafManifest(JsonElement manifest)
        => manifest.TryGetProperty("hhaf", out var hhaf) && hhaf.ValueKind == JsonValueKind.Object
            ? hhaf
            : manifest;

    private static JsonElement? HhafInstall(JsonElement manifest)
    {
        var hhaf = HhafManifest(manifest);
        return hhaf.TryGetProperty("install", out var install) && install.ValueKind == JsonValueKind.Object
            ? install
            : null;
    }

    private static string? ManifestUrl(JsonElement manifest)
    {
        return HhafInstall(manifest) is { } install && JsonString(install, "manifestUrl") is { } manifestUrl
            ? manifestUrl
            : JsonString(manifest, "manifestUrl");
    }

    private static IReadOnlyList<string> ManifestCommands(string appKey, JsonElement manifest)
    {
        _ = appKey;
        return HhafInstall(manifest) is { } install &&
            install.TryGetProperty("commands", out var commands) &&
            commands.ValueKind == JsonValueKind.Array
            ? commands.EnumerateArray()
                .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString())
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToArray()
            : (IReadOnlyList<string>)[];
    }

    private static object? ManifestHotCheck(string appKey, JsonElement manifest)
    {
        _ = appKey;
        if (HhafInstall(manifest) is { } install &&
            install.TryGetProperty("hotCheck", out var hotCheck) &&
            hotCheck.ValueKind == JsonValueKind.Object)
        {
            var command = JsonString(hotCheck, "command");
            var args = hotCheck.TryGetProperty("args", out var argsElement) && argsElement.ValueKind == JsonValueKind.Array
                ? argsElement.EnumerateArray()
                    .Select(value => value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString())
                    .ToArray()
                : [];
            return string.IsNullOrWhiteSpace(command) ? null : new { command, args };
        }

        return null;
    }

    public sealed record InstallAppRequest(Guid? FamilyId, string AppKey);
    public sealed record SetAppStateRequest(string? State);
    public sealed record AppApplyResultRequest(IReadOnlyList<AppItemResult>? Apps);
    public sealed record AppItemResult(
        Guid Id,
        string? RuntimeState,
        string? DesiredState,
        string? InstalledVersion,
        string? ActiveVersion,
        bool? RequiresReboot,
        string? Error);
}
