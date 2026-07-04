using System.Text.Json;
using System.Text.Json.Serialization;

namespace HomeHarbor.Tooling;

public sealed record HomeHarborBootState(
    string DefaultSlot,
    string DefaultRootSlot,
    string? NextSlot,
    string? NextRootSlot,
    string? NextMode,
    string RecoverySlot,
    long Generation,
    string UpdatedAt);

public static class BootState
{
    public const string RelativePath = "EFI/HomeHarbor/boot_state.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static string PathForEsp(string espPath)
        => Path.Combine(espPath, RelativePath.Replace('/', Path.DirectorySeparatorChar));

    public static HomeHarborBootState Read(string espPath)
    {
        var path = PathForEsp(espPath);
        if (!File.Exists(path))
        {
            return New("A", "A", "A");
        }

        using var stream = File.OpenRead(path);
        var state = JsonSerializer.Deserialize<HomeHarborBootState>(stream, JsonOptions) ?? New("A", "A", "A");
        return Normalize(state);
    }

    public static void Write(string espPath, HomeHarborBootState state)
    {
        var normalized = Normalize(state) with
        {
            Generation = Math.Max(0, state.Generation),
            UpdatedAt = string.IsNullOrWhiteSpace(state.UpdatedAt) ? Now() : state.UpdatedAt
        };
        FileWrites.AtomicWriteText(PathForEsp(espPath), JsonSerializer.Serialize(normalized, JsonOptions) + "\n", 0644);
    }

    public static HomeHarborBootState New(string defaultSlot, string defaultRootSlot, string recoverySlot)
        => Normalize(new HomeHarborBootState(defaultSlot, defaultRootSlot, null, null, null, recoverySlot, 1, Now()));

    public static void Initialize(string espPath, string defaultSlot = "A", string defaultRootSlot = "A", string recoverySlot = "A")
        => Write(espPath, New(defaultSlot, defaultRootSlot, recoverySlot));

    public static void SetDefault(string espPath, string bootSlot, string? rootSlot = null)
    {
        var state = Read(espPath);
        Write(espPath, state with
        {
            DefaultSlot = NormalizeSlot(bootSlot),
            DefaultRootSlot = NormalizeSlot(rootSlot ?? bootSlot),
            NextSlot = null,
            NextRootSlot = null,
            NextMode = null,
            Generation = state.Generation + 1,
            UpdatedAt = Now()
        });
    }

    public static void SetOneShot(string espPath, string bootSlot, string rootSlot, string mode = "normal")
    {
        var normalizedMode = NormalizeMode(mode);
        var state = Read(espPath);
        Write(espPath, state with
        {
            NextSlot = NormalizeSlot(bootSlot),
            NextRootSlot = normalizedMode == "normal" ? NormalizeSlot(rootSlot) : null,
            NextMode = normalizedMode == "normal" ? null : normalizedMode,
            Generation = state.Generation + 1,
            UpdatedAt = Now()
        });
    }

    public static void SetRecovery(string espPath, string slot)
    {
        var state = Read(espPath);
        Write(espPath, state with
        {
            RecoverySlot = NormalizeSlot(slot),
            Generation = state.Generation + 1,
            UpdatedAt = Now()
        });
    }

    public static void ClearNext(string espPath)
    {
        var state = Read(espPath);
        Write(espPath, state with
        {
            NextSlot = null,
            NextRootSlot = null,
            NextMode = null,
            Generation = state.Generation + 1,
            UpdatedAt = Now()
        });
    }

    private static HomeHarborBootState Normalize(HomeHarborBootState state)
    {
        var defaultSlot = string.IsNullOrWhiteSpace(state.DefaultSlot) ? "A" : state.DefaultSlot;
        var defaultRootSlot = string.IsNullOrWhiteSpace(state.DefaultRootSlot) ? defaultSlot : state.DefaultRootSlot;
        var recoverySlot = string.IsNullOrWhiteSpace(state.RecoverySlot) ? defaultSlot : state.RecoverySlot;

        return state with
        {
            DefaultSlot = NormalizeSlot(defaultSlot),
            DefaultRootSlot = NormalizeSlot(defaultRootSlot),
            NextSlot = string.IsNullOrWhiteSpace(state.NextSlot) ? null : NormalizeSlot(state.NextSlot),
            NextRootSlot = string.IsNullOrWhiteSpace(state.NextRootSlot) ? null : NormalizeSlot(state.NextRootSlot),
            NextMode = string.IsNullOrWhiteSpace(state.NextMode) ? null : NormalizeMode(state.NextMode),
            RecoverySlot = NormalizeSlot(recoverySlot),
            UpdatedAt = string.IsNullOrWhiteSpace(state.UpdatedAt) ? Now() : state.UpdatedAt
        };
    }

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

    private static string Now()
        => DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
}
