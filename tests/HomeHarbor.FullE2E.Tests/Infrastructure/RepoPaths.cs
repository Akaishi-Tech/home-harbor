namespace HomeHarbor.FullE2E.Tests.Infrastructure;

internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string Artifacts => Path.Combine(Root, "artifacts");

    public static string Work => Path.Combine(Root, ".work", "full-e2e");

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "HomeHarbor.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not find HomeHarbor.slnx from the test output directory.");
    }
}
