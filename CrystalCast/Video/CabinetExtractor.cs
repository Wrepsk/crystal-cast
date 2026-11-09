using System.ComponentModel;
using System.Runtime.InteropServices;

namespace CrystalCast.Video;

/// <summary>
/// Unpacks a .cab archive with setupapi's SetupIterateCabinetW, which Windows and Wine both provide, so no
/// extra library or child process is needed.
/// </summary>
internal static class CabinetExtractor
{
    private const uint SpFileNotifyFileInCabinet = 0x11;
    private const uint SpFileNotifyNeedNewCabinet = 0x12;
    private const uint SpFileNotifyFileExtracted = 0x13;
    private const uint FileOpAbort = 0;
    private const uint FileOpDoCopy = 1;
    private const int MaxPath = 260;

    // FILE_IN_CABINET_INFO_W: NameInCabinet (pointer), FileSize, Win32Error, DosDate, DosTime, DosAttribs,
    // then FullTargetName[MAX_PATH]. The offsets are the same with 1- and 8-byte packing.
    private static readonly int FileSizeOffset = IntPtr.Size;
    private static readonly int FullTargetNameOffset = IntPtr.Size + 4 + 4 + 2 + 2 + 2;

    // FILEPATHS_W: Target, Source (pointers), Win32Error, Flags.
    private static readonly int ExtractedErrorOffset = IntPtr.Size * 2;

    private delegate uint FileCallback(IntPtr context, uint notification, IntPtr param1, IntPtr param2);

    /// <summary>
    /// Extracts every file. <paramref name="mapTarget"/> turns a path inside the cabinet into a path on disk,
    /// or returns null to skip the file. <paramref name="progress"/> gets the bytes written so far.
    /// </summary>
    public static void Extract(
        string cabinetPath,
        Func<string, string?> mapTarget,
        Action<long>? progress,
        CancellationToken cancellationToken)
    {
        Exception? failure = null;
        long extractedBytes = 0;
        long pendingBytes = 0;

        uint Callback(IntPtr context, uint notification, IntPtr param1, IntPtr param2)
        {
            try
            {
                switch (notification)
                {
                    case SpFileNotifyFileInCabinet:
                    {
                        if (cancellationToken.IsCancellationRequested)
                            return FileOpAbort;

                        var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(param1)) ?? string.Empty;
                        var target = mapTarget(name);
                        if (target == null)
                            return 2; // FILEOP_SKIP

                        var fullTarget = Path.GetFullPath(target);
                        if (fullTarget.Length >= MaxPath)
                            throw new PathTooLongException($"The path for {name} is too long: {fullTarget}");

                        Directory.CreateDirectory(Path.GetDirectoryName(fullTarget)!);
                        var chars = (fullTarget + '\0').ToCharArray();
                        Marshal.Copy(chars, 0, param1 + FullTargetNameOffset, chars.Length);
                        pendingBytes = (uint)Marshal.ReadInt32(param1 + FileSizeOffset);
                        return FileOpDoCopy;
                    }

                    case SpFileNotifyFileExtracted:
                    {
                        var error = Marshal.ReadInt32(param1 + ExtractedErrorOffset);
                        if (error != 0)
                        {
                            var target = Marshal.PtrToStringUni(Marshal.ReadIntPtr(param1)) ?? "a file";
                            throw new Win32Exception(error, $"Could not write {target}: {new Win32Exception(error).Message}");
                        }

                        extractedBytes += pendingBytes;
                        progress?.Invoke(extractedBytes);
                        return 0;
                    }

                    case SpFileNotifyNeedNewCabinet:
                        throw new InvalidDataException("The archive is split across several files, which is not supported.");

                    default:
                        return 0;
                }
            }
            catch (Exception ex)
            {
                failure ??= ex;
                return notification == SpFileNotifyFileInCabinet ? FileOpAbort : 1; // ERROR_INVALID_FUNCTION stops the walk
            }
        }

        FileCallback callback = Callback;
        var succeeded = SetupIterateCabinetW(cabinetPath, 0, callback, IntPtr.Zero);
        var lastError = Marshal.GetLastWin32Error();
        GC.KeepAlive(callback);

        cancellationToken.ThrowIfCancellationRequested();
        if (failure != null)
            throw new IOException($"Unpacking {Path.GetFileName(cabinetPath)} failed: {failure.Message}", failure);
        if (!succeeded)
            throw new Win32Exception(lastError, $"Unpacking {Path.GetFileName(cabinetPath)} failed: {new Win32Exception(lastError).Message}");
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupIterateCabinetW(string cabinetFile, uint reserved, FileCallback msgHandler, IntPtr context);
}
