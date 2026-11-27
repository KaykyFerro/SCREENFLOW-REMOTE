using System.Runtime.InteropServices;
using System.Text;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Infrastructure.Security;

/// <summary>
/// (1.5.0) DPAPI da conta do Windows (CryptProtectData): o segredo só abre nesta conta, neste PC. Chamado direto no
/// crypt32 (sem pacote novo). A "entropia" amarra o segredo ao ScreenFlow: outro app que só chame o DPAPI não abre.
/// </summary>
public sealed class WindowsSecretProtector : ISecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ScreenFlow.Settings.v1");
    private const int UiForbidden = 0x1;

    public string Scheme => "dpapi";

    public byte[] Protect(byte[] data) => Run(data, protect: true);

    public byte[] Unprotect(byte[] data) => Run(data, protect: false);

    private static unsafe byte[] Run(byte[] data, bool protect)
    {
        fixed (byte* input = data)
        fixed (byte* entropy = Entropy)
        {
            var inBlob = new DataBlob { Size = data.Length, Data = (nint)input };
            var entropyBlob = new DataBlob { Size = Entropy.Length, Data = (nint)entropy };
            var outBlob = default(DataBlob);
            var ok = protect
                ? CryptProtectData(ref inBlob, null, ref entropyBlob, 0, 0, UiForbidden, ref outBlob)
                : CryptUnprotectData(ref inBlob, 0, ref entropyBlob, 0, 0, UiForbidden, ref outBlob);
            if (!ok) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[outBlob.Size];
                Marshal.Copy(outBlob.Data, result, 0, outBlob.Size);
                return result;
            }
            finally
            {
                if (outBlob.Data != 0)
                {
                    // Limpa a cópia em texto puro que o Windows devolveu antes de soltar a memória.
                    if (!protect) new Span<byte>((void*)outBlob.Data, outBlob.Size).Clear();
                    LocalFree(outBlob.Data);
                }
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int Size;
        public nint Data;
    }

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, ref DataBlob dataOut);

    [DllImport("kernel32.dll")]
    private static extern nint LocalFree(nint memory);
}
