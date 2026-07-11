using HomeHarbor.Api.Auth;
using HomeHarbor.Api.Data;
using HomeHarbor.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HomeHarbor.Api.Controllers;

[ApiController]
[Route("api/containers")]
public sealed class ContainersController(
    HomeHarborDbContext db,
    IFamilyResolver families,
    IManagedContainerSpecService specs,
    IRuntimeSignalService signals) : ControllerBase
{
    [HttpGet]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> List([FromQuery] Guid? familyId, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(familyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var containers = await db.ManagedContainers
            .AsNoTracking()
            .Where(c => c.FamilyId == resolved.Value && c.DeletedAt == null)
            .OrderBy(c => c.Name)
            .ToListAsync(cancellationToken);
        return Ok(containers.Select(ContainerResponse));
    }

    [HttpGet("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var container = await db.ManagedContainers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (container is null || container.DeletedAt is not null) return NotFound(new { error = "Container not found." });
        await families.RequireAccessAsync(container.FamilyId, cancellationToken);
        return Ok(ContainerResponse(container));
    }

    [HttpPost]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Create([FromBody] MutateContainerRequest request, CancellationToken cancellationToken)
    {
        var resolved = await families.ResolveAsync(request.FamilyId, cancellationToken);
        if (resolved is null) return BadRequest(new { error = "Create a family space first." });

        var id = Guid.NewGuid();
        ContainerDefinition definition;
        try
        {
            definition = specs.Normalize(resolved.Value, id, request.ToDefinitionRequest());
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }

        await ContainerMutationGate.Instance.WaitAsync(cancellationToken);
        try
        {
            var existing = await db.ManagedContainers.AsNoTracking()
                .Where(container => container.DeletedAt == null)
                .ToListAsync(cancellationToken);
            if (existing.Any(container => container.FamilyId == resolved.Value && container.Name == definition.Name))
                return Conflict(new { error = "A container with this name already exists." });
            try
            {
                specs.EnsurePortsAvailable(definition, existing);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { error = ex.Message });
            }

            var now = DateTimeOffset.UtcNow;
            var container = new ManagedContainerEntity
            {
                Id = id,
                FamilyId = resolved.Value,
                Name = definition.Name,
                Image = definition.Image,
                DesiredState = "stopped",
                RuntimeState = "planned",
                RequestedAction = "reload",
                ServiceName = $"homeharbor-{id:N}",
                DefinitionJson = specs.Serialize(definition),
                CreatedAt = now,
                UpdatedAt = now
            };
            _ = db.ManagedContainers.Add(container);
            _ = await db.SaveChangesAsync(cancellationToken);
            signals.RequestContainerApply();
            return Ok(ContainerResponse(container));
        }
        finally
        {
            _ = ContainerMutationGate.Instance.Release();
        }
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Update(Guid id, [FromBody] MutateContainerRequest request, CancellationToken cancellationToken)
    {
        await ContainerMutationGate.Instance.WaitAsync(cancellationToken);
        try
        {
            var container = await db.ManagedContainers.FindAsync([id], cancellationToken);
            if (container is null || container.DeletedAt is not null) return NotFound(new { error = "Container not found." });
            await families.RequireAccessAsync(container.FamilyId, cancellationToken);

            ContainerDefinition definition;
            try
            {
                definition = specs.Normalize(container.FamilyId, container.Id, request.ToDefinitionRequest());
            }
            catch (InvalidOperationException ex)
            {
                return BadRequest(new { error = ex.Message });
            }

            var existing = await db.ManagedContainers.AsNoTracking()
                .Where(candidate => candidate.DeletedAt == null)
                .ToListAsync(cancellationToken);
            if (!string.Equals(container.Name, definition.Name, StringComparison.Ordinal) &&
                existing.Any(candidate => candidate.FamilyId == container.FamilyId && candidate.Name == definition.Name))
            {
                return Conflict(new { error = "A container with this name already exists." });
            }
            try
            {
                specs.EnsurePortsAvailable(definition, existing, container.Id);
            }
            catch (InvalidOperationException ex)
            {
                return Conflict(new { error = ex.Message });
            }

            container.Name = definition.Name;
            container.Image = definition.Image;
            container.DefinitionJson = specs.Serialize(definition);
            container.RequestedAction = container.DesiredState == "running" ? "restart" : "reload";
            container.RuntimeState = "pending";
            container.LastError = string.Empty;
            container.UpdatedAt = DateTimeOffset.UtcNow;
            _ = await db.SaveChangesAsync(cancellationToken);
            signals.RequestContainerApply();
            return Ok(ContainerResponse(container));
        }
        finally
        {
            _ = ContainerMutationGate.Instance.Release();
        }
    }

    [HttpPost("{id:guid}/start")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public Task<IActionResult> Start(Guid id, CancellationToken cancellationToken)
        => SetDesiredState(id, "running", "start", cancellationToken);

    [HttpPost("{id:guid}/stop")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public Task<IActionResult> Stop(Guid id, CancellationToken cancellationToken)
        => SetDesiredState(id, "stopped", "stop", cancellationToken);

    [HttpPost("{id:guid}/restart")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public Task<IActionResult> Restart(Guid id, CancellationToken cancellationToken)
        => SetDesiredState(id, "running", "restart", cancellationToken);

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = AuthorizationPolicies.FamilyAdmin)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var container = await db.ManagedContainers.FindAsync([id], cancellationToken);
        if (container is null || container.DeletedAt is not null) return NotFound(new { error = "Container not found." });
        await families.RequireAccessAsync(container.FamilyId, cancellationToken);

        container.DesiredState = "deleted";
        container.RequestedAction = "delete";
        container.RuntimeState = "pending-delete";
        container.DeletedAt = DateTimeOffset.UtcNow;
        container.UpdatedAt = DateTimeOffset.UtcNow;
        _ = await db.SaveChangesAsync(cancellationToken);
        signals.RequestContainerApply();
        return Ok(ContainerResponse(container));
    }

    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpGet("reconcile/desired")]
    public async Task<IActionResult> Desired(CancellationToken cancellationToken)
    {
        var containers = await db.ManagedContainers
            .AsNoTracking()
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(cancellationToken);
        return Ok(containers.Select(c => new
        {
            c.Id,
            c.FamilyId,
            c.Name,
            c.Image,
            c.DesiredState,
            c.RuntimeState,
            c.RequestedAction,
            c.ServiceName,
            unitName = $"{c.ServiceName}.service",
            quadletFile = $"{c.ServiceName}.container",
            c.DeletedAt,
            definition = specs.Deserialize(c.DefinitionJson),
            quadlet = c.DesiredState == "deleted" ? string.Empty : specs.BuildQuadlet(c)
        }));
    }

    [Authorize(Policy = AuthorizationPolicies.Automation)]
    [HttpPost("reconcile/result")]
    public async Task<IActionResult> ApplyResult([FromBody] ContainerApplyResultRequest request, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var item in request.Containers ?? [])
        {
            var container = await db.ManagedContainers.FindAsync([item.Id], cancellationToken);
            if (container is null) continue;
            container.RuntimeState = string.IsNullOrWhiteSpace(item.RuntimeState) ? container.DesiredState : item.RuntimeState.Trim();
            container.RequestedAction = "none";
            container.LastError = item.Error ?? string.Empty;
            container.LastAppliedAt = now;
            if (container.RuntimeState == "deleted") container.DeletedAt ??= now;
        }

        _ = await db.SaveChangesAsync(cancellationToken);
        return Ok(new { appliedAt = now });
    }

    private async Task<IActionResult> SetDesiredState(Guid id, string desiredState, string action, CancellationToken cancellationToken)
    {
        var container = await db.ManagedContainers.FindAsync([id], cancellationToken);
        if (container is null || container.DeletedAt is not null) return NotFound(new { error = "Container not found." });
        await families.RequireAccessAsync(container.FamilyId, cancellationToken);

        container.DesiredState = desiredState;
        container.RequestedAction = action;
        container.RuntimeState = "pending";
        container.LastError = string.Empty;
        container.UpdatedAt = DateTimeOffset.UtcNow;
        _ = await db.SaveChangesAsync(cancellationToken);
        signals.RequestContainerApply();
        return Ok(ContainerResponse(container));
    }

    private object ContainerResponse(ManagedContainerEntity container)
        => new
        {
            container.Id,
            container.FamilyId,
            container.Name,
            container.Image,
            container.DesiredState,
            container.RuntimeState,
            container.RequestedAction,
            container.ServiceName,
            container.LastError,
            container.CreatedAt,
            container.UpdatedAt,
            container.DeletedAt,
            container.LastAppliedAt,
            definition = specs.Deserialize(container.DefinitionJson)
        };

    public sealed record MutateContainerRequest(
        Guid? FamilyId,
        string? Name,
        string? Image,
        IReadOnlyDictionary<string, string>? Environment,
        IReadOnlyList<ContainerPortRequest>? Ports,
        IReadOnlyList<ContainerVolumeRequest>? Volumes,
        IReadOnlyList<string>? Command,
        bool? Privileged,
        string? Network,
        IReadOnlyList<string>? Devices,
        IReadOnlyList<string>? Capabilities,
        string? PodmanArgs)
    {
        public ContainerDefinitionRequest ToDefinitionRequest()
            => new(Name, Image, Environment, Ports, Volumes, Command, Privileged, Network, Devices, Capabilities, PodmanArgs);
    }

    public sealed record ContainerApplyResultRequest(IReadOnlyList<ContainerItemResult>? Containers);
    public sealed record ContainerItemResult(Guid Id, string? RuntimeState, string? Error);
}
