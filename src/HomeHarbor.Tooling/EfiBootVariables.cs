namespace HomeHarbor.Tooling;

public static class EfiBootVariables
{
    public const string VendorGuid = "8be4df61-93ca-4d2c-bb7c-0f9d9aee5f3a";
    public const string BootNextName = "HomeHarborBootNext";
    public const string BootCurrentName = "HomeHarborBootCurrent";
    public const string DataUnlockModeName = "HomeHarborDataUnlockMode";
    public const string FullBootNextName = VendorGuid + "-" + BootNextName;
    public const string FullBootCurrentName = VendorGuid + "-" + BootCurrentName;
    public const string FullDataUnlockModeName = VendorGuid + "-" + DataUnlockModeName;

    private const string DefaultAttributes = "7";

    public static string BuildOneShotPayload(string bootSlot, string rootSlot, string mode = "normal")
    {
        var normalizedMode = NormalizeMode(mode);
        var normalizedBootSlot = NormalizeSlot(bootSlot);
        if (normalizedMode == "recovery")
        {
            return "recovery:" + normalizedBootSlot;
        }

        var normalizedRootSlot = NormalizeSlot(rootSlot);
        return "normal:" + normalizedBootSlot + ":" + normalizedRootSlot;
    }

    public static async Task SetOneShotAsync(
        ICommandRunner runner,
        string bootSlot,
        string rootSlot,
        string mode = "normal",
        CancellationToken cancellationToken = default)
    {
        var payload = BuildOneShotPayload(bootSlot, rootSlot, mode);
        var result = await WriteBootNextAsync(runner, payload, cancellationToken);
        _ = result.EnsureSuccess("failed to write HomeHarbor EFI boot-next variable");
    }

    public static async Task ClearOneShotAsync(
        ICommandRunner runner,
        CancellationToken cancellationToken = default)
    {
        _ = await WriteBootNextAsync(runner, string.Empty, cancellationToken);
    }

    public static async Task SetDataUnlockModeAsync(
        ICommandRunner runner,
        string unlockMode,
        CancellationToken cancellationToken = default)
    {
        var normalized = unlockMode.Trim().ToLowerInvariant() switch
        {
            "passphrase" => "passphrase",
            "tpm2" => "tpm2",
            _ => throw new ArgumentException("unlock mode must be passphrase or tpm2", nameof(unlockMode))
        };
        var result = await WriteVariableAsync(runner, FullDataUnlockModeName, normalized, cancellationToken);
        _ = result.EnsureSuccess("failed to write HomeHarbor data unlock mode EFI variable");
    }

    private static async Task<CommandResult> WriteBootNextAsync(
        ICommandRunner runner,
        string payload,
        CancellationToken cancellationToken)
        => await WriteVariableAsync(runner, FullBootNextName, payload, cancellationToken);

    private static async Task<CommandResult> WriteVariableAsync(
        ICommandRunner runner,
        string fullName,
        string payload,
        CancellationToken cancellationToken)
    {
        var dataFile = Path.Combine(Path.GetTempPath(), "homeharbor-efivar-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(dataFile, payload, cancellationToken);
            return await runner.RunAsync(
                EfivarCommand(),
                ["-w", "-A", EfivarAttributes(), "-n", fullName, "-f", dataFile],
                new CommandRunOptions(Timeout: TimeSpan.FromSeconds(15)),
                cancellationToken);
        }
        finally
        {
            TryDelete(dataFile);
        }
    }

    private static string EfivarCommand()
        => Environment.GetEnvironmentVariable("HOMEHARBOR_EFIVAR") is { Length: > 0 } value ? value : "efivar";

    private static string EfivarAttributes()
        => Environment.GetEnvironmentVariable("HOMEHARBOR_EFIVAR_ATTRIBUTES") is { Length: > 0 } value ? value : DefaultAttributes;

    private static string NormalizeSlot(string slot)
        => slot.Trim().ToUpperInvariant() switch
        {
            "A" => "A",
            "B" => "B",
            _ => throw new ArgumentException("slot must be A or B", nameof(slot))
        };

    private static string NormalizeMode(string mode)
        => mode.Trim().ToLowerInvariant() switch
        {
            "normal" => "normal",
            "recovery" => "recovery",
            _ => throw new ArgumentException("boot mode must be normal or recovery", nameof(mode))
        };

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
