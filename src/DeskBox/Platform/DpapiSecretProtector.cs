using System.Runtime.InteropServices;
using System.Text;

namespace DeskBox.Platform;

/// <summary>
/// Per-Windows-user DPAPI (<c>CryptProtectData</c> / <c>CryptUnprotectData</c>) secret protector.
/// Isolated in <c>DeskBox.Platform</c> so <c>Crypt32.dll</c> / <c>Kernel32.dll</c> P/Invoke stays
/// inside the platform boundary and remains 100% NativeAOT-compatible.
/// </summary>
internal static class DpapiSecretProtector
{
    private const uint CryptProtectUiForbidden = 0x1;

    internal static unsafe string ProtectUtf8ToBase64(string plaintext)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            return string.Empty;
        }

        byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
        fixed (byte* pIn = plainBytes)
        {
            var inBlob = new DataBlob
            {
                cbData = plainBytes.Length,
                pbData = (nint)pIn
            };
            if (!CryptProtectData(ref inBlob, null, nint.Zero, nint.Zero, nint.Zero, CryptProtectUiForbidden, out DataBlob outBlob))
            {
                return string.Empty;
            }

            try
            {
                byte[] encrypted = new byte[outBlob.cbData];
                Marshal.Copy(outBlob.pbData, encrypted, 0, outBlob.cbData);
                return Convert.ToBase64String(encrypted);
            }
            finally
            {
                if (outBlob.pbData != nint.Zero)
                {
                    LocalFree(outBlob.pbData);
                }
            }
        }
    }

    internal static unsafe string UnprotectBase64ToUtf8(string base64Cipher)
    {
        if (string.IsNullOrWhiteSpace(base64Cipher))
        {
            return string.Empty;
        }

        try
        {
            byte[] cipherBytes = Convert.FromBase64String(base64Cipher);
            if (cipherBytes.Length == 0)
            {
                return string.Empty;
            }

            fixed (byte* pIn = cipherBytes)
            {
                var inBlob = new DataBlob
                {
                    cbData = cipherBytes.Length,
                    pbData = (nint)pIn
                };
                if (!CryptUnprotectData(ref inBlob, nint.Zero, nint.Zero, nint.Zero, nint.Zero, CryptProtectUiForbidden, out DataBlob outBlob))
                {
                    return string.Empty;
                }

                try
                {
                    byte[] plain = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, plain, 0, outBlob.cbData);
                    return Encoding.UTF8.GetString(plain);
                }
                finally
                {
                    if (outBlob.pbData != nint.Zero)
                    {
                        LocalFree(outBlob.pbData);
                    }
                }
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public nint pbData;
    }

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn,
        string? szDataDescr,
        nint pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("Crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn,
        nint ppszDataDescr,
        nint pOptionalEntropy,
        nint pvReserved,
        nint pPromptStruct,
        uint dwFlags,
        out DataBlob pDataOut);

    [DllImport("Kernel32.dll", SetLastError = true)]
    private static extern nint LocalFree(nint hMem);
}
