using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrystalCast.Sync;

internal static class IpcJsonService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        Converters = { new ScreenSourceKindJsonConverter(), new JsonStringEnumConverter() },
    };

    public static T? Deserialize<T>(string json)
    {
        return JsonSerializer.Deserialize<T>(json, JsonOptions);
    }

    public static string Serialize<T>(T value)
    {
        return JsonSerializer.Serialize(value, JsonOptions);
    }

    public static string SerializeMutationSuccess(BrowserScreenProfile screen, bool created)
    {
        return Serialize(new ScreenIpcMutationResponse
        {
            Success = true,
            Created = created,
            Updated = !created,
            ScreenId = screen.ScreenId,
            Screen = BuildScreenSummary(screen),
        });
    }

    public static string SerializeMutationError(string error, string screenId = "")
    {
        return Serialize(new ScreenIpcMutationResponse
        {
            Success = false,
            Error = error,
            ScreenId = NormalizeText(screenId),
        });
    }

    public static ScreenIpcScreenSummary BuildScreenSummary(BrowserScreenProfile screen)
    {
        return new ScreenIpcScreenSummary
        {
            ScreenId = screen.ScreenId,
            Name = screen.Name,
            Enabled = screen.Enabled,
            CreatedByIpc = screen.CreatedByIpc,
            OwnerId = screen.IpcOwnerId,
            SourceControlsLocked = screen.SourceControlsLocked,
            SourceControlsOwnerId = screen.SourceControlsOwnerId,
            Provider = screen.ProviderKind.ToString(),
            Placement = BuildPlacementState(screen.Placement),
        };
    }

    public static ScreenPlacementStateDto BuildPlacementState(ScreenPlacementSettings placement)
    {
        return new ScreenPlacementStateDto
        {
            Mode = placement.Mode,
            PositionX = placement.PositionX,
            PositionY = placement.PositionY,
            PositionZ = placement.PositionZ,
            YawRadians = placement.YawRadians,
            PitchRadians = placement.PitchRadians,
            RollRadians = placement.RollRadians,
            WidthMeters = placement.WidthMeters,
            HeightMeters = placement.HeightMeters,
            ScreenCurveAmountMeters = placement.ScreenCurveAmountMeters,
            OccludedAlpha = placement.OccludedAlpha,
            OcclusionTolerance = placement.OcclusionTolerance,
            DistanceFadeEnabled = placement.EnableDistanceFade,
            FadeStartMeters = placement.FadeStartMeters,
            FadeStopMeters = placement.FadeStopMeters,
        };
    }

    public static string NormalizeText(string? value)
    {
        return value?.Trim() ?? string.Empty;
    }

    public static string FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            var normalized = NormalizeText(value);
            if (!string.IsNullOrWhiteSpace(normalized))
                return normalized;
        }

        return string.Empty;
    }
}
