using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace CrystalCast.Twitch;

/// <summary>What is kept between sessions for a logged-in Twitch account.</summary>
internal sealed record TwitchTokens(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset ExpiresAt,
    string Login,
    string UserId,
    string DisplayName);

/// <summary>
/// Keeps the Twitch login in its own file next to the plugin's settings, encrypted with Windows' per-user
/// encryption (DPAPI), so it never appears in the settings file, diagnostics or shared configs. Where that
/// encryption isn't available, nothing is written and the login lasts until the game closes.
/// </summary>
internal sealed class TwitchTokenStore(string directory)
{
    private const string FileName = "twitch-login.bin";
    private static readonly byte[] Entropy = "CrystalCast Twitch login"u8.ToArray();

    private string FilePath => Path.Combine(directory, FileName);

    /// <summary>False when the last save couldn't encrypt, so the login won't survive a restart.</summary>
    public bool Persistent { get; private set; } = true;

    public TwitchTokens? Load()
    {
        try
        {
            if (!File.Exists(FilePath))
                return null;

            var json = Unprotect(File.ReadAllBytes(FilePath));
            return json == null ? null : JsonSerializer.Deserialize<TwitchTokens>(json);
        }
        catch
        {
            return null;
        }
    }

    public void Save(TwitchTokens tokens)
    {
        var protectedBytes = Protect(JsonSerializer.SerializeToUtf8Bytes(tokens));
        Persistent = protectedBytes != null;
        if (protectedBytes == null)
        {
            Delete();
            return;
        }

        Directory.CreateDirectory(directory);
        var temp = FilePath + ".tmp";
        File.WriteAllBytes(temp, protectedBytes);
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Delete()
    {
        try
        {
            File.Delete(FilePath);
        }
        catch
        {
            // Nothing there, or locked; the next save replaces it.
        }
    }

    private static byte[]? Protect(byte[] data) => Crypt(data, protect: true);

    private static byte[]? Unprotect(byte[] data) => Crypt(data, protect: false);

    private static byte[]? Crypt(byte[] data, bool protect)
    {
        if (!OperatingSystem.IsWindows())
            return null;

        var input = Pin(data);
        var entropy = Pin(Entropy);
        var output = default(DataBlob);
        try
        {
            var ok = protect
                ? CryptProtectData(ref input.Blob, null, ref entropy.Blob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output)
                : CryptUnprotectData(ref input.Blob, IntPtr.Zero, ref entropy.Blob, IntPtr.Zero, IntPtr.Zero, UiForbidden, ref output);
            if (!ok || output.Data == IntPtr.Zero)
                return null;

            var result = new byte[output.Size];
            Marshal.Copy(output.Data, result, 0, output.Size);
            return result;
        }
        catch
        {
            // Wine without a working crypt32, for example.
            return null;
        }
        finally
        {
            input.Handle.Free();
            entropy.Handle.Free();
            if (output.Data != IntPtr.Zero)
                LocalFree(output.Data);
        }
    }

    private static (GCHandle Handle, DataBlob Blob) Pin(byte[] data)
    {
        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        return (handle, new DataBlob { Size = data.Length, Data = handle.AddrOfPinnedObject() });
    }

    private const int UiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public IntPtr Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, IntPtr description, ref DataBlob entropy, IntPtr reserved, IntPtr prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
