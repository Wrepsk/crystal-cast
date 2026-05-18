using System.Numerics;
using System.Text.Json.Serialization;

namespace CrystalCast.Sync;

public sealed class ScreenStateEnvelope
{
    public int SchemaVersion { get; set; } = 1;
    public string ScreenId { get; set; } = string.Empty;
    public string OwnerSessionId { get; set; } = string.Empty;
    public ushort TerritoryId { get; set; }
    public Vector3Dto Position { get; set; } = new();
    public QuaternionDto Rotation { get; set; } = QuaternionDto.Identity;
    public Vector2Dto SizeMeters { get; set; } = new(3.0f, 1.6875f);
    public ScreenSourceState Source { get; set; } = new();
    public ScreenPlaybackStateDto Playback { get; set; } = new();
    public ScreenVisualState Visual { get; set; } = new();
    public long TimestampUnixMs { get; set; }
    public long Sequence { get; set; }
}

public sealed class ScreenSourceState
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScreenSourceKind Kind { get; set; }

    public string Provider { get; set; } = string.Empty;
    public string Identity { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Hash { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string VideoId { get; set; } = string.Empty;
}

public sealed class ScreenPlaybackStateDto
{
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ScreenPlaybackState State { get; set; } = ScreenPlaybackState.Playing;

    public long PositionMs { get; set; }
    public long DurationMs { get; set; }
    public float Rate { get; set; } = 1.0f;
    public long HostTimestampUnixMs { get; set; }
}

public sealed class ScreenVisualState
{
    public float OccludedAlpha { get; set; }
    public float OcclusionTolerance { get; set; } = 0.02f;
    public float ScreenCurveAmountMeters { get; set; }
    public bool DistanceFadeEnabled { get; set; }
    public float FadeStartMeters { get; set; } = 35.0f;
    public float FadeStopMeters { get; set; } = 60.0f;
}

public readonly record struct Vector2Dto(float X, float Y);

public readonly record struct Vector3Dto(float X, float Y, float Z)
{
    public static Vector3Dto FromVector3(Vector3 value) => new(value.X, value.Y, value.Z);
    public Vector3 ToVector3() => new(X, Y, Z);
}

public readonly record struct QuaternionDto(float X, float Y, float Z, float W)
{
    public static readonly QuaternionDto Identity = new(0, 0, 0, 1);
    public static QuaternionDto FromQuaternion(Quaternion value) => new(value.X, value.Y, value.Z, value.W);
    public Quaternion ToQuaternion() => new(X, Y, Z, W);
}
