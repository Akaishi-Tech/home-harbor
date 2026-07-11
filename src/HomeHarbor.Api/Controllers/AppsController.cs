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
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
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
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Install([FromBody] InstallAppRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });
        var template = await catalog.FindAsync(request.AppKey, cancellationToken);
        if (template is null || !template.IsVisibleTo(CurrentRole())) return NotFound(new { error = "Unknown app." });
        if (template.Kind == "system")
        {
            return !CurrentUserIsAdmin() ? Forbid() : SystemAppOperationUnavailable();
        }
        if (!template.Available)
        {
            var error = string.IsNullOrWhiteSpace(template.UnavailableReason)
                ? "App is not available on this appliance."
                : template.UnavailableReason;
            return Conflict(new { error });
        }

        return await InstallContainerAppAsync(resolved.Value, template, cancellationToken);
    }

    [HttpDelete("installs/{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var app = await db.ManagedApps.FindAsync([id], cancellationToken);
        if (app is null) return NotFound(new { error = "App install not found." });
        await families.RequireAccessAsync(app.FamilyId, cancellationToken);
        if (app.Kind == "system" && !CurrentUserIsAdmin()) return Forbid();
        if (app.Kind == "system") return SystemAppOperationUnavailable();

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
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public IActionResult SetState(Guid id, [FromBody] SetAppStateRequest request, CancellationToken cancellationToken)
    {
        _ = id;
        _ = request;
        _ = cancellationToken;
        return StatusCode(StatusCodes.Status501NotImplemented, new
        {
            error = "App state changes are unavailable because this endpoint is not connected to the container or system-app runtime. No state was changed."
        });
    }

    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpGet("reconcile/desired")]
    public IActionResult Desired(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Ok(Array.Empty<object>());
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
        await ContainerMutationGate.Instance.WaitAsync(cancellationToken);
        try
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
                var activeContainers = await db.ManagedContainers.AsNoTracking()
                    .Where(candidate => candidate.DeletedAt == null)
                    .ToListAsync(cancellationToken);
                if (activeContainers.Any(candidate => candidate.FamilyId == familyId && candidate.Name == definition.Name))
                {
                    return Conflict(new { error = "A container with this app name already exists." });
                }
                try
                {
                    specs.EnsurePortsAvailable(definition, activeContainers);
                }
                catch (InvalidOperationException ex)
                {
                    return Conflict(new { error = ex.Message });
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
        finally
        {
            _ = ContainerMutationGate.Instance.Release();
        }
    }

    internal static IActionResult SystemAppOperationUnavailable()
        => new ObjectResult(new
        {
            error = "System app operations are unavailable until persistent activation and boot-time wrapper reconstruction are implemented. No operation was queued."
        })
        {
            StatusCode = StatusCodes.Status501NotImplemented
        };

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
