using System.Text.Json;
using System.Text.Json.Serialization;

namespace CrystalCast;

[JsonConverter(typeof(ScreenSourceKindJsonConverter))]
public enum ScreenSourceKind
{
    // Deserialization-only compatibility values for version 1 configuration and IPC payloads.
    [Obsolete("Local video sources are no longer supported.")]
    LocalVideo = 2,
    [Obsolete("Use Browser.")]
    YouTubeBrowser = 3,
    Browser = 3,
}

public sealed class ScreenSourceKindJsonConverter : JsonConverter<ScreenSourceKind>
{
    public override ScreenSourceKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numericValue))
            return ReadNumeric(numericValue);

        if (reader.TokenType != JsonTokenType.String)
            throw new JsonException("Screen source kind must be a string or integer.");

        return reader.GetString() switch
        {
            "Browser" or "YouTubeBrowser" => ScreenSourceKind.Browser,
            "LocalVideo" => (ScreenSourceKind)2,
            _ => throw new JsonException("Unsupported screen source kind."),
        };
    }

    public override void Write(Utf8JsonWriter writer, ScreenSourceKind value, JsonSerializerOptions options)
    {
        if (value == ScreenSourceKind.Browser)
        {
            writer.WriteStringValue("Browser");
            return;
        }

        if ((int)value == 2)
        {
            writer.WriteStringValue("LocalVideo");
            return;
        }

        throw new JsonException($"Unsupported screen source kind value '{(int)value}'.");
    }

    private static ScreenSourceKind ReadNumeric(int value)
    {
        return value switch
        {
            2 => (ScreenSourceKind)2,
            3 => ScreenSourceKind.Browser,
            _ => throw new JsonException($"Unsupported screen source kind value '{value}'."),
        };
    }
}

public enum ScreenPlaybackState
{
    Stopped = 0,
    Playing = 1,
    Paused = 2,
}
