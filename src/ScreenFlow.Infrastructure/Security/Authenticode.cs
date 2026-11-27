using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace ScreenFlow.Infrastructure.Security;

public enum AuthenticodeKind { Valid, Unsigned, Invalid }

public readonly record struct AuthenticodeStatus(AuthenticodeKind Kind, int Code, string? Signer);

/// <summary>Assinatura digital (Authenticode) de um arquivo, conferida pelo Windows (WinVerifyTrust).</summary>
public static class Authenticode
{
    /// <summary>TRUST_E_NOSIGNATURE: o arquivo não tem assinatura.</summary>
    public const int NoSignature = unchecked((int)0x800B0100);

    /// <summary>Válida (com quem assinou), sem assinatura, ou inválida (adulterado, certificado não confiável).</summary>
    public static AuthenticodeStatus Verify(string path)
    {
        var code = TrustCode(path);
        if (code == NoSignature) return new AuthenticodeStatus(AuthenticodeKind.Unsigned, code, null);
        if (code != 0) return new AuthenticodeStatus(AuthenticodeKind.Invalid, code, null);
        try
        {
#pragma warning disable SYSLIB0057 // o certificado de quem assinou o arquivo (o Windows já validou acima)
            using var certificate = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return new AuthenticodeStatus(AuthenticodeKind.Valid, 0, certificate.GetNameInfo(X509NameType.SimpleName, false));
        }
        catch (Exception exception) when (exception is System.Security.Cryptography.CryptographicException or IOException)
        {
            return new AuthenticodeStatus(AuthenticodeKind.Invalid, code, null);
        }
    }

    // ───────────────────────── WinVerifyTrust ─────────────────────────

    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public string FilePath;
        public IntPtr File;
        public IntPtr KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public IntPtr PolicyCallbackData;
        public IntPtr SipClientData;
        public uint UiChoice;           // WTD_UI_NONE = 2
        public uint RevocationChecks;   // WTD_REVOKE_NONE = 0
        public uint UnionChoice;        // WTD_CHOICE_FILE = 1
        public IntPtr FileInfo;
        public uint StateAction;        // WTD_STATEACTION_IGNORE = 0
        public IntPtr StateData;
        public IntPtr UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public IntPtr SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr window, [MarshalAs(UnmanagedType.LPStruct)] Guid action, ref WinTrustData data);

    /// <summary>0 quando a assinatura é válida e confiável para o Windows; senão o código do erro (HRESULT).</summary>
    public static int TrustCode(string path)
    {
        var fileInfo = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = path };
        var pointer = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pointer, false);
            var data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = 2,
                RevocationChecks = 0,
                UnionChoice = 1,
                FileInfo = pointer,
                StateAction = 0,
            };
            return WinVerifyTrust(new IntPtr(-1), GenericVerifyV2, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(pointer);
            Marshal.FreeHGlobal(pointer);
        }
    }
}
