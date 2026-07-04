using System.Text;

namespace HomeHarbor.Tooling;

public sealed record HomeHarborBootEnvironment(
    string BootSlot,
    string RootSlot,
    string RootLogical,
    string KernelRelease,
    string ModulesLogical,
    string FirmwareLogical,
    string VbmetaPartition,
    string VbmetaDigest,
    string RootDescriptorDigest,
    string ModulesDescriptorDigest,
    string FirmwareDescriptorDigest,
    string Version);

public static class BootEnvironment
{
    public static IReadOnlyDictionary<string, string> Read(string path)
    {
        if (!File.Exists(path))
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            var equals = line.IndexOf('=');
            if (equals <= 0)
            {
                continue;
            }

            result[line[..equals]] = line[(equals + 1)..];
        }

        return result;
    }

    public static string? ReadValue(string path, string key)
        => Read(path).TryGetValue(key, out var value) ? value : null;

    public static string ToText(HomeHarborBootEnvironment env)
    {
        var builder = new StringBuilder();
        Append(builder, "HOMEHARBOR_BOOT_SLOT", env.BootSlot);
        Append(builder, "HOMEHARBOR_SLOT", env.RootSlot);
        Append(builder, "HOMEHARBOR_ROOT_LOGICAL", env.RootLogical);
        Append(builder, "HOMEHARBOR_KERNEL_RELEASE", env.KernelRelease);
        Append(builder, "HOMEHARBOR_MODULES_LOGICAL", env.ModulesLogical);
        Append(builder, "HOMEHARBOR_FIRMWARE_LOGICAL", env.FirmwareLogical);
        Append(builder, "HOMEHARBOR_VBMETA_PARTITION", env.VbmetaPartition);
        Append(builder, "HOMEHARBOR_VBMETA_DIGEST", env.VbmetaDigest);
        Append(builder, "HOMEHARBOR_ROOT_DESCRIPTOR_DIGEST", env.RootDescriptorDigest);
        Append(builder, "HOMEHARBOR_MODULES_DESCRIPTOR_DIGEST", env.ModulesDescriptorDigest);
        Append(builder, "HOMEHARBOR_FIRMWARE_DESCRIPTOR_DIGEST", env.FirmwareDescriptorDigest);
        Append(builder, "HOMEHARBOR_VERSION", env.Version);
        return builder.ToString();
    }

    public static void Write(string path, HomeHarborBootEnvironment env)
        => FileWrites.AtomicWriteText(path, ToText(env), 0644);

    private static void Append(StringBuilder builder, string key, string value)
    {
        _ = builder.Append(key);
        _ = builder.Append('=');
        _ = builder.AppendLine(value);
    }
}
