using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;
using Barista.Services;

namespace Barista;

/// <summary>
/// What can be said about Barista.exe itself.
///
/// The hashes on the integrity page come from a manifest inside this exe, so
/// they prove the unpacked files are the ones the exe carries - not that the
/// exe is the one that was released. That second half is the Authenticode
/// signature and the SHA256SUMS file on the release page, and this is where
/// the page gets both numbers to show next to them.
/// </summary>
public static class SelfCheck
{
    public enum SignatureState { Valid, Unsigned, Untrusted, Tampered, Unknown }

    public sealed record Result(string Path, string? Sha256, SignatureState Signature, string Detail);

    public static Result Run()
    {
        var path = Environment.ProcessPath ?? "";
        if (path.Length == 0 || !File.Exists(path))
            return new(path, null, SignatureState.Unknown, "Could not find this program's own file.");

        string? hash = null;
        try { hash = PayloadVerifier.Sha256OfFile(path); }
        catch { /* shown as "could not be read" */ }

        var (state, detail) = Signature(path);
        return new(path, hash, state, detail);
    }

    private static (SignatureState, string) Signature(string path)
    {
        int hr;
        try
        {
            hr = Verify(path);
        }
        catch (Exception ex)
        {
            return (SignatureState.Unknown, $"The signature could not be checked: {ex.Message}");
        }

        switch ((uint)hr)
        {
            case 0:
                return (SignatureState.Valid, $"Signed by {Signer(path) ?? "an unnamed publisher"}, and Windows trusts the signature.");
            case 0x800B0100:    // TRUST_E_NOSIGNATURE
            case 0x800B0003:    // TRUST_E_SUBJECT_FORM_UNKNOWN
            case 0x800B0001:    // TRUST_E_PROVIDER_UNKNOWN
                return (SignatureState.Unsigned,
                    "Not signed. Compare the SHA-256 above with SHA256SUMS on the release page before going on.");
            case 0x80096010:    // TRUST_E_BAD_DIGEST
                return (SignatureState.Tampered,
                    "Signed, but the file has been changed since. Do not use this copy.");
            default:
                return (SignatureState.Untrusted,
                    $"Signed by {Signer(path) ?? "an unnamed publisher"}, but Windows does not trust "
                    + $"the signature (0x{hr:X8}). Compare the SHA-256 with SHA256SUMS before going on.");
        }
    }

    private static string? Signer(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            var subject = X509Certificate.CreateFromSignedFile(path).Subject;
#pragma warning restore SYSLIB0057
            var cn = subject.Split(',').Select(p => p.Trim())
                            .FirstOrDefault(p => p.StartsWith("CN=", StringComparison.OrdinalIgnoreCase));
            return cn?[3..] ?? subject;
        }
        catch
        {
            return null;
        }
    }

    // --- WinVerifyTrust --------------------------------------------------------
    // The same check Explorer's "Digital Signatures" tab does. No UI, no
    // revocation fetch: this may run on a VM with no network at all, and a
    // revocation check that times out would only make the answer slower.

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
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

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid action, ref WinTrustData data);

    private static int Verify(string path)
    {
        const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1;
        const uint WTD_STATEACTION_VERIFY = 1, WTD_STATEACTION_CLOSE = 2;
        const uint WTD_CACHE_ONLY_URL_RETRIEVAL = 0x1000;

        var pathPtr = Marshal.StringToHGlobalUni(path);
        var filePtr = IntPtr.Zero;
        try
        {
            var file = new WinTrustFileInfo
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustFileInfo>(),
                pcwszFilePath = pathPtr,
            };
            filePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, filePtr, false);

            var data = new WinTrustData
            {
                cbStruct = (uint)Marshal.SizeOf<WinTrustData>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = filePtr,
                dwStateAction = WTD_STATEACTION_VERIFY,
                dwProvFlags = WTD_CACHE_ONLY_URL_RETRIEVAL,
            };

            var action = GenericVerifyV2;
            var hr = WinVerifyTrust(IntPtr.Zero, ref action, ref data);

            data.dwStateAction = WTD_STATEACTION_CLOSE;
            WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return hr;
        }
        finally
        {
            if (filePtr != IntPtr.Zero) Marshal.FreeHGlobal(filePtr);
            Marshal.FreeHGlobal(pathPtr);
        }
    }
}
