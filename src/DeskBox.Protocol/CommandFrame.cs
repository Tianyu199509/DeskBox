using System.Buffers.Binary;

namespace DeskBox.Protocol;

/// <summary>
/// Length-prefixed frame codec shared by the in-app pipe server and the CLI
/// client: a 4-byte little-endian payload length followed by that many bytes
/// of UTF-8 JSON. A shared codec (instead of newline delimiting) keeps the
/// transport immune to embedded newlines and to payload-size ambiguity.
/// </summary>
public static class CommandFrame
{
    public const int LengthPrefixBytes = 4;

    public static async Task WriteAsync(
        Stream stream,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken = default)
    {
        if (payload.Length == 0)
        {
            throw new ArgumentException("Frame payload must not be empty.", nameof(payload));
        }

        if (payload.Length > CommandApiProtocol.MaxFrameBytes)
        {
            throw new ArgumentException(
                $"Frame payload of {payload.Length} bytes exceeds the {CommandApiProtocol.MaxFrameBytes}-byte limit.",
                nameof(payload));
        }

        byte[] prefix = new byte[LengthPrefixBytes];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] prefix = new byte[LengthPrefixBytes];
        await ReadExactlyAsync(stream, prefix, cancellationToken).ConfigureAwait(false);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
        if (length == 0)
        {
            throw new InvalidDataException("Received an empty command frame.");
        }

        if (length > CommandApiProtocol.MaxFrameBytes)
        {
            throw new InvalidDataException(
                $"Received a {length}-byte frame that exceeds the {CommandApiProtocol.MaxFrameBytes}-byte limit.");
        }

        byte[] payload = new byte[length];
        await ReadExactlyAsync(stream, payload, cancellationToken).ConfigureAwait(false);
        return payload;
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken cancellationToken)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[totalRead..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException(
                    $"Stream ended after {totalRead} of {buffer.Length} expected bytes.");
            }

            totalRead += read;
        }
    }
}
