namespace HomeHarbor.Tooling;

public static class ContainerRuntimePaths
{
    public const string DefaultHome = "/var/lib/homeharbor-containers";
    public const string RootManagedQuadletDirectory = DefaultHome + "/.config/containers/systemd";
    public const string PodmanConfigHome = DefaultHome + "/.config/containers/runtime";
    public const string QuadletPodmanConfigEnvironment = "XDG_CONFIG_HOME=%h/.config/containers/runtime";
}
