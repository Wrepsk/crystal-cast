using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Text.Json;

namespace CrystalCast.Video;

internal static class LinuxBrowserProtocol
{
    public const int MaximumWidth = 1920;
    public const int MaximumHeight = 1080;
    public const int SlotSize = MaximumWidth * MaximumHeight * 4;
    public const int SlotCount = 3;
    public const int MaximumJsonBytes = 512 * 1024;
    public const uint Magic = 0x31424343;

    public static async Task WriteJsonAsync(Stream stream, string json, CancellationToken cancellationToken)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(json);
        if (bytes.Length is 0 or > MaximumJsonBytes)
            throw new InvalidDataException("Native browser command exceeds the protocol limit.");
        var header = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(header, Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), bytes.Length);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<(int Kind, int Length)> ReadHeaderAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[16];
        await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
        var kind = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8));
        if (BinaryPrimitives.ReadUInt32LittleEndian(header) != Magic
            || BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(12)) != 0
            || (kind != 1 && kind != 2) || length <= 0
            || length > (kind == 1 ? MaximumJsonBytes : SlotSize + 32))
            throw new InvalidDataException("Invalid native browser packet header.");
        return (kind, length);
    }

    public static void ValidateFrame(int width, int height, int stride, long sequence)
    {
        if (width is < 1 or > MaximumWidth || height is < 1 or > MaximumHeight
            || stride != width * 4 || sequence <= 0)
            throw new InvalidDataException("Invalid native browser frame layout.");
    }

    public static async Task<VideoFrame> ReadFrameAsync(Stream stream, int length, CancellationToken cancellationToken, long sequenceBase = 0)
    {
        if (length < 32) throw new InvalidDataException("Truncated native browser frame metadata.");
        var metadata = new byte[32];
        await stream.ReadExactlyAsync(metadata, cancellationToken).ConfigureAwait(false);
        var width = BinaryPrimitives.ReadInt32LittleEndian(metadata);
        var height = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(4));
        var stride = BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(8));
        var sequence = BinaryPrimitives.ReadInt64LittleEndian(metadata.AsSpan(16));
        ValidateFrame(width, height, stride, sequence);
        if (BinaryPrimitives.ReadInt32LittleEndian(metadata.AsSpan(12)) != 1 || length != 32 + stride * height)
            throw new InvalidDataException("Invalid native browser pixel format or payload length.");
        var frame = VideoFrame.Rent(width, height, checked(sequence + sequenceBase), BinaryPrimitives.ReadInt64LittleEndian(metadata.AsSpan(24)));
        try
        {
            await stream.ReadExactlyAsync(frame.Pixels.AsMemory(0, frame.PixelLength), cancellationToken).ConfigureAwait(false);
            return frame;
        }
        catch
        {
            frame.Dispose();
            throw;
        }
    }

    public static async Task<JsonDocument> ReadJsonAsync(Stream stream, int length, CancellationToken cancellationToken)
    {
        var bytes = new byte[length];
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new InvalidDataException("Native browser message must be a JSON object.");
        }
        return document;
    }
}

internal sealed class LinuxBrowserFrameMapping : IDisposable
{
    private readonly MemoryMappedFile file;
    private readonly MemoryMappedViewAccessor view;

    public LinuxBrowserFrameMapping(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        try
        {
            if (stream.Length != LinuxBrowserProtocol.SlotSize * LinuxBrowserProtocol.SlotCount)
                throw new InvalidDataException("Native browser shared-frame file has the wrong size.");
            file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        }
        catch { stream.Dispose(); throw; }
        try { view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read); }
        catch { file.Dispose(); throw; }
    }

    public VideoFrame Read(JsonElement message, long sequenceBase = 0)
    {
        var width = message.GetProperty("width").GetInt32();
        var height = message.GetProperty("height").GetInt32();
        var stride = message.GetProperty("stride").GetInt32();
        var sequence = message.GetProperty("sequence").GetInt64();
        var slot = message.GetProperty("slot").GetInt32();
        LinuxBrowserProtocol.ValidateFrame(width, height, stride, sequence);
        if (slot is < 0 or >= LinuxBrowserProtocol.SlotCount)
            throw new InvalidDataException("Invalid native browser shared-frame slot.");
        var frame = VideoFrame.Rent(width, height, checked(sequence + sequenceBase), message.GetProperty("timestamp").GetInt64());
        try
        {
            if (view.ReadArray((long)slot * LinuxBrowserProtocol.SlotSize, frame.Pixels, 0, frame.PixelLength) != frame.PixelLength)
                throw new EndOfStreamException("Native browser shared frame is incomplete.");
            return frame;
        }
        catch { frame.Dispose(); throw; }
    }

    public void Dispose() { view.Dispose(); file.Dispose(); }
}
