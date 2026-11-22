using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace DeskBox.Features.Clipboard;

/// <summary>
/// Deterministic content hashing for clipboard dedup and self-write suppression.
/// Hashes are HMAC-SHA256 under a machine+user-local DPAPI-protected key when one is
/// supplied (h1: prefix) so persisted digests cannot confirm candidate plaintexts
/// offline, while staying deterministic on-device for dedup.
/// </summary>
internal static class ClipboardContentHashing
{
    internal static string ComputeNormalizedTextContentHash(ReadOnlySpan<char> text, byte[]? hmacKey = null)
    {
        const int ChunkChars = 4096;
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        if (text.Length <= 512 && text.IndexOf('\r') < 0)
        {
            Span<byte> utf8Small = stackalloc byte[text.Length * 3];
            ReadOnlySpan<byte> utf8 = utf8Small[..Encoding.UTF8.GetBytes(text, utf8Small)];
            if (hmacKey is not null)
            {
                HMACSHA256.HashData(hmacKey, utf8, digest);
            }
            else
            {
                SHA256.HashData(utf8, digest);
            }
        }
        else
        {
            byte[] rentedUtf8 = ArrayPool<byte>.Shared.Rent(ChunkChars * 3);
            try
            {
                using var hasher = hmacKey is not null
                    ? IncrementalHash.CreateHMAC(HashAlgorithmName.SHA256, hmacKey)
                    : IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                Span<char> normChars = stackalloc char[ChunkChars];
                ReadOnlySpan<char> rem = text;
                while (!rem.IsEmpty)
                {
                    if (rem.IndexOf('\r') < 0)
                    {
                        int take = Math.Min(rem.Length, ChunkChars);
                        if (take < rem.Length && char.IsHighSurrogate(rem[take - 1]) && char.IsLowSurrogate(rem[take])) take--;
                        hasher.AppendData(rentedUtf8.AsSpan(0, Encoding.UTF8.GetBytes(rem[..take], rentedUtf8)));
                        rem = rem[take..];
                        continue;
                    }

                    int normLen = 0;
                    while (!rem.IsEmpty && normLen < ChunkChars - 1)
                    {
                        int cr = rem.IndexOf('\r');
                        if (cr != 0)
                        {
                            int take = Math.Min(cr < 0 ? rem.Length : cr, ChunkChars - normLen);
                            if (take < rem.Length && take > 0 && char.IsHighSurrogate(rem[take - 1]) && char.IsLowSurrogate(rem[take])) take--;
                            rem[..take].CopyTo(normChars[normLen..]);
                            normLen += take;
                            rem = rem[take..];
                            if (cr < 0 || take < cr || normLen >= ChunkChars - 1) break;
                        }

                        bool isCrLf = rem.Length >= 2 && rem[1] == '\n';
                        normChars[normLen++] = isCrLf ? '\n' : '\r';
                        rem = rem[(isCrLf ? 2 : 1)..];
                    }

                    hasher.AppendData(rentedUtf8.AsSpan(0, Encoding.UTF8.GetBytes(normChars[..normLen], rentedUtf8)));
                }

                hasher.GetHashAndReset(digest);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rentedUtf8);
            }
        }

        string prefix = hmacKey is not null ? "txt:h1:" : "txt:";
        Span<char> outChars = stackalloc char[prefix.Length + (SHA256.HashSizeInBytes * 2)];
        prefix.CopyTo(outChars);
        Convert.TryToHexStringLower(digest, outChars[prefix.Length..], out _);
        return new string(outChars);
    }

    internal static string ComputeImageContentHash(byte[] pngBytes, byte[]? hmacKey = null)
    {
        string hex = hmacKey is not null
            ? Convert.ToHexStringLower(HMACSHA256.HashData(hmacKey, pngBytes))
            : Convert.ToHexStringLower(SHA256.HashData(pngBytes));
        return hmacKey is not null ? $"img:h1:{hex}" : $"img:{hex}";
    }

    internal static string ComputeFilesContentHash(IReadOnlyList<string> paths, byte[]? hmacKey = null)
    {
        string[] cleaned = paths
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Trim())
            .ToArray();
        string normalizedForHash = string.Join(
            '\n',
            cleaned.Select(p => p.TrimEnd('\\', '/').ToUpperInvariant()).OrderBy(p => p, StringComparer.Ordinal));
        byte[] utf8 = Encoding.UTF8.GetBytes(normalizedForHash);
        string hex = hmacKey is not null
            ? Convert.ToHexStringLower(HMACSHA256.HashData(hmacKey, utf8))
            : Convert.ToHexStringLower(SHA256.HashData(utf8));
        return hmacKey is not null ? $"files:h1:{hex}" : $"files:{hex}";
    }

    internal static string MediaFileNameFromContentHash(string contentHash) =>
        contentHash[(contentHash.LastIndexOf(':') + 1)..] + ".png";
}