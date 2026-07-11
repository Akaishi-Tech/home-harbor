namespace HomeHarbor.Api.Services;

internal static class ContainerMutationGate
{
    internal static readonly SemaphoreSlim Instance = new(1, 1);
}
