using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        try
        {
            fixed (byte* pIn = plainBytes)
            {
                var inBlob = new DataBlob
                {
                    cbData = plainBytes.Length,
                    pbData = (nint)pIn
                };
                if (!CryptProtectData(ref inBlob, null, nint.Zero, nint.Zero, nint.Zero, CryptProtectUiForbidden, out DataBlob outBlob))
                {
                    int win32Error = Marshal.GetLastWin32Error();
                    throw new CryptographicException(win32Error != 0 ? win32Error : -2147467259);
                }

                try
                {
                    if (outBlob.cbData <= 0 || outBlob.pbData == nint.Zero)
                    {
                        throw new CryptographicException("DPAPI CryptProtectData returned an empty cipher blob.");
                    }

                    return Convert.ToBase64String(new ReadOnlySpan<byte>((void*)outBlob.pbData, outBlob.cbData));
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
        finally
        {
            CryptographicOperations.ZeroMemory(plainBytes);
        }
    }

    internal static bool TryProtectUtf8ToBase64(string plaintext, out string base64Cipher)
    {
        if (string.IsNullOrEmpty(plaintext))
        {
            base64Cipher = string.Empty;
            return true;
        }

        try
        {
            base64Cipher = ProtectUtf8ToBase64(plaintext);
            return !string.IsNullOrEmpty(base64Cipher);
        }
        catch (CryptographicException)
        {
            base64Cipher = string.Empty;
            return false;
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
                    if (outBlob.cbData <= 0 || outBlob.pbData == nint.Zero)
                    {
                        return string.Empty;
                    }

                    return Encoding.UTF8.GetString((byte*)outBlob.pbData, outBlob.cbData);
                }
                finally
                {
                    if (outBlob.pbData != nint.Zero)
                    {
                        if (outBlob.cbData > 0)
                        {
                            NativeMemory.Clear((void*)outBlob.pbData, (nuint)outBlob.cbData);
                        }

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

    internal static unsafe byte[] ProtectBytes(ReadOnlySpan<byte> plainBytes)
    {
        if (plainBytes.IsEmpty)
        {
            return [];
        }

        fixed (byte* pIn = plainBytes)
        {
            var inBlob = new DataBlob
            {
                cbData = plainBytes.Length,
                pbData = (nint)pIn
            };
            if (!CryptProtectData(ref inBlob, null, nint.Zero, nint.Zero, nint.Zero, CryptProtectUiForbidden, out DataBlob outBlob))
            {
                int win32Error = Marshal.GetLastWin32Error();
                throw new CryptographicException(win32Error != 0 ? win32Error : -2147467259);
            }

            try
            {
                if (outBlob.cbData <= 0 || outBlob.pbData == nint.Zero)
                {
                    throw new CryptographicException("DPAPI CryptProtectData returned an empty cipher blob.");
                }

                return new ReadOnlySpan<byte>((void*)outBlob.pbData, outBlob.cbData).ToArray();
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

    internal static unsafe bool TryUnprotectBytes(ReadOnlySpan<byte> cipherBytes, out byte[] plainBytes)
    {
        plainBytes = [];
        if (cipherBytes.IsEmpty)
        {
            return false;
        }

        try
        {
            fixed (byte* pIn = cipherBytes)
            {
                var inBlob = new DataBlob
                {
                    cbData = cipherBytes.Length,
                    pbData = (nint)pIn
                };
                if (!CryptUnprotectData(ref inBlob, nint.Zero, nint.Zero, nint.Zero, nint.Zero, CryptProtectUiForbidden, out DataBlob outBlob))
                {
                    return false;
                }

                try
                {
                    if (outBlob.cbData <= 0 || outBlob.pbData == nint.Zero)
                    {
                        return false;
                    }

                    plainBytes = new ReadOnlySpan<byte>((void*)outBlob.pbData, outBlob.cbData).ToArray();
                    return true;
                }
                finally
                {
                    if (outBlob.pbData != nint.Zero)
                    {
                        if (outBlob.cbData > 0)
                        {
                            NativeMemory.Clear((void*)outBlob.pbData, (nuint)outBlob.cbData);
                        }

                        LocalFree(outBlob.pbData);
                    }
                }
            }
        }
        catch
        {
            plainBytes = [];
            return false;
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
