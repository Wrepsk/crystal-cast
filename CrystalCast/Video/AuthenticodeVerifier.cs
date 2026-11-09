using System.Runtime.InteropServices;

namespace CrystalCast.Video;

internal enum AuthenticodeResult
{
    /// <summary>Signed, unmodified, and the signature chains to a trusted root.</summary>
    Trusted,

    /// <summary>
    /// Signed and unmodified, but the root certificate is not trusted on this system. Wine prefixes take
    /// their roots from Linux, which does not carry Microsoft's code-signing root.
    /// </summary>
    UntrustedRoot,

    /// <summary>Unsigned, modified after signing, or not readable as a signed file.</summary>
    Invalid,
}

/// <summary>Checks a file's Authenticode signature with WinVerifyTrust, without network access or UI.</summary>
internal static class AuthenticodeVerifier
{
    private const int TrustENoSignature = unchecked((int)0x800B0100);
    private const int TrustEBadDigest = unchecked((int)0x80096010);
    private const int CertEUntrustedRoot = unchecked((int)0x800B0109);
    private const int CertEChaining = unchecked((int)0x800B010A);
    private const int TrustEProviderUnknown = unchecked((int)0x800B0001);
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    public static AuthenticodeResult Verify(string path, out int status)
    {
        var pathPointer = Marshal.StringToHGlobalUni(path);
        var fileInfo = IntPtr.Zero;
        var trustData = IntPtr.Zero;
        try
        {
            var file = new WintrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustFileInfo>(),
                pcwszFilePath = pathPointer,
            };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustFileInfo>());
            Marshal.StructureToPtr(file, fileInfo, false);

            var data = new WintrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WintrustData>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pFile = fileInfo,
                dwStateAction = 1, // WTD_STATEACTION_VERIFY
                dwProvFlags = 0x10 | 0x1000, // no revocation check, cached URLs only
            };
            trustData = Marshal.AllocHGlobal(Marshal.SizeOf<WintrustData>());
            Marshal.StructureToPtr(data, trustData, false);

            var action = GenericVerifyV2;
            status = WinVerifyTrust(new IntPtr(-1), ref action, trustData);

            // WTD_STATEACTION_CLOSE releases what the verify step allocated.
            var closing = Marshal.PtrToStructure<WintrustData>(trustData);
            closing.dwStateAction = 2;
            Marshal.StructureToPtr(closing, trustData, false);
            WinVerifyTrust(new IntPtr(-1), ref action, trustData);

            return status switch
            {
                0 => AuthenticodeResult.Trusted,
                CertEUntrustedRoot or CertEChaining or TrustEProviderUnknown => AuthenticodeResult.UntrustedRoot,
                _ => AuthenticodeResult.Invalid,
            };
        }
        finally
        {
            if (trustData != IntPtr.Zero)
                Marshal.FreeHGlobal(trustData);
            if (fileInfo != IntPtr.Zero)
                Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(pathPointer);
        }
    }

    public static string Describe(int status) => status switch
    {
        0 => "signature verified",
        TrustENoSignature => "the file is not signed",
        TrustEBadDigest => "the file was changed after it was signed",
        CertEUntrustedRoot => "signed, but this system does not trust the signing root",
        CertEChaining => "signed, but the certificate chain could not be built",
        TrustEProviderUnknown => "this system cannot check signatures of this file type",
        _ => $"signature check failed (0x{status:X8})",
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WintrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, IntPtr data);
}
