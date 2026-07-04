namespace HomeHarbor.Tooling;

public static class AvbPartitionNames
{
    public static string DescriptorName(string partitionName)
        => partitionName.EndsWith("_a", StringComparison.Ordinal) || partitionName.EndsWith("_b", StringComparison.Ordinal)
            ? partitionName[..^2]
            : partitionName;
}
