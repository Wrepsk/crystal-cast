using CrystalCast.Video;

namespace CrystalCast.Sync;

internal static class ScreenStateValidator
{
    private const int MaxIdLength = 128;
    private const int MaxTextLength = 512;
    private const int MaxUrlLength = 4096;
    private const float MaxWorldCoordinate = 100_000.0f;
    private const long MaxFutureClockSkewMs = 5 * 60 * 1000;

    public static bool TryValidate(ScreenStateEnvelope? state, long nowUnixMs, out string error)
    {
        if (state == null)
            return Fail("State is required.", out error);
        if (state.SchemaVersion != 1)
            return Fail($"Unsupported schema version '{state.SchemaVersion}'.", out error);
        if (!IsRequiredText(state.ScreenId, MaxIdLength))
            return Fail("ScreenId is required and must be at most 128 characters.", out error);
        if (!IsRequiredText(state.OwnerSessionId, MaxIdLength))
            return Fail("OwnerSessionId is required and must be at most 128 characters.", out error);
        if (state.Sequence < 0)
            return Fail("Sequence cannot be negative.", out error);
        if (state.TimestampUnixMs < 0 || state.TimestampUnixMs > nowUnixMs + MaxFutureClockSkewMs)
            return Fail("TimestampUnixMs is outside the accepted range.", out error);

        if (!IsFiniteAndBounded(state.Position.X, -MaxWorldCoordinate, MaxWorldCoordinate)
            || !IsFiniteAndBounded(state.Position.Y, -MaxWorldCoordinate, MaxWorldCoordinate)
            || !IsFiniteAndBounded(state.Position.Z, -MaxWorldCoordinate, MaxWorldCoordinate))
            return Fail("Position contains an invalid coordinate.", out error);

        if (!IsFiniteAndBounded(state.SizeMeters.X, 0.1f, 100.0f)
            || !IsFiniteAndBounded(state.SizeMeters.Y, 0.1f, 100.0f))
            return Fail("SizeMeters must be finite and between 0.1 and 100 meters.", out error);

        if (!IsFinite(state.Rotation.X, state.Rotation.Y, state.Rotation.Z, state.Rotation.W))
            return Fail("Rotation contains a non-finite component.", out error);
        var rotationLengthSquared = (state.Rotation.X * state.Rotation.X)
            + (state.Rotation.Y * state.Rotation.Y)
            + (state.Rotation.Z * state.Rotation.Z)
            + (state.Rotation.W * state.Rotation.W);
        if (rotationLengthSquared is < 0.25f or > 4.0f)
            return Fail("Rotation is not a usable quaternion.", out error);

        if (!TryValidateSource(state.Source, out error)
            || !TryValidatePlayback(state.Playback, nowUnixMs, out error)
            || !TryValidateVisual(state.Visual, out error))
            return false;

        error = string.Empty;
        return true;
    }

    private static bool TryValidateSource(ScreenSourceState? source, out string error)
    {
        if (source == null)
            return Fail("Source is required.", out error);
        if (source.Kind != ScreenSourceKind.Browser)
            return Fail("Only browser sources are supported.", out error);
        if (!Enum.TryParse<BrowserSourceProviderKind>(source.Provider, true, out var provider)
            || !BrowserSourceProviderRegistry.IsSupported(provider))
            return Fail($"Unsupported browser source provider '{source.Provider}'.", out error);
        if (!IsOptionalText(source.Identity, MaxTextLength)
            || !IsOptionalText(source.Title, MaxTextLength)
            || !IsOptionalText(source.Hash, MaxTextLength)
            || !IsOptionalText(source.VideoId, MaxTextLength))
            return Fail("Source metadata exceeds the accepted length.", out error);
        if (!IsOptionalText(source.Url, MaxUrlLength))
            return Fail("Source URL exceeds the accepted length.", out error);
        if (!string.IsNullOrWhiteSpace(source.Url)
            && (!Uri.TryCreate(source.Url, UriKind.Absolute, out var uri)
                || (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))))
            return Fail("Source URL must use HTTP or HTTPS.", out error);

        error = string.Empty;
        return true;
    }

    private static bool TryValidatePlayback(ScreenPlaybackStateDto? playback, long nowUnixMs, out string error)
    {
        if (playback == null)
            return Fail("Playback is required.", out error);
        if (!Enum.IsDefined(playback.State))
            return Fail("Playback state is invalid.", out error);
        if (playback.PositionMs < 0 || playback.DurationMs < 0)
            return Fail("Playback position and duration cannot be negative.", out error);
        if (!IsFiniteAndBounded(playback.Rate, 0.1f, 4.0f))
            return Fail("Playback rate must be finite and between 0.1 and 4.0.", out error);
        if (playback.HostTimestampUnixMs < 0 || playback.HostTimestampUnixMs > nowUnixMs + MaxFutureClockSkewMs)
            return Fail("Playback host timestamp is outside the accepted range.", out error);

        error = string.Empty;
        return true;
    }

    private static bool TryValidateVisual(ScreenVisualState? visual, out string error)
    {
        if (visual == null)
            return Fail("Visual is required.", out error);
        if (!IsFiniteAndBounded(visual.OccludedAlpha, 0.0f, 1.0f)
            || !IsFiniteAndBounded(visual.OcclusionTolerance, 0.0f, 10.0f)
            || !IsFiniteAndBounded(visual.ScreenCurveAmountMeters, 0.0f, 50.0f)
            || !IsFiniteAndBounded(visual.FadeStartMeters, 0.0f, MaxWorldCoordinate)
            || !IsFiniteAndBounded(visual.FadeStopMeters, visual.FadeStartMeters, MaxWorldCoordinate))
            return Fail("Visual settings are outside the accepted range.", out error);

        error = string.Empty;
        return true;
    }

    private static bool IsRequiredText(string? value, int maxLength)
        => !string.IsNullOrWhiteSpace(value)
            && value.Length <= maxLength
            && string.Equals(value, value.Trim(), StringComparison.Ordinal);

    private static bool IsOptionalText(string? value, int maxLength)
        => value != null && value.Length <= maxLength;

    private static bool IsFiniteAndBounded(float value, float minimum, float maximum)
        => float.IsFinite(value) && value >= minimum && value <= maximum;

    private static bool IsFinite(params float[] values) => values.All(float.IsFinite);

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
