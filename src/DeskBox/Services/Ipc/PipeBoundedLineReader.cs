using System.Buffers;
using System.Text;
using System.Text.Json;

namespace DeskBox.Services.Ipc;

internal enum BoundedLineReadKind
{
    Line = 0,
    EndOfStream = 1,
    Oversized = 2
}

internal readonly record struct BoundedLineReadResult(
    BoundedLineReadKind Kind,
    string? Line,
    int ObservedLength)
{
    public static BoundedLineReadResult FromLine(string line) =>
        new(BoundedLineReadKind.Line, line, line.Length);

    public static BoundedLineReadResult Eof =>
        new(BoundedLineReadKind.EndOfStream, null, 0);

    public static BoundedLineReadResult FromOversized(int observedLength) =>
        new(BoundedLineReadKind.Oversized, null, observedLength);
}

/// <summary>
/// Allocation-bounded, SIMD-accelerated newline-delimited reader over a
/// <see cref="StreamReader"/> backed by an 8 KB <see cref="ArrayPool{T}"/>
/// buffer. Enforces <c>maxChars</c> before materializing oversized payloads,
/// distinguishes normal EOF from oversized lines, and reuses a single linked
/// <see cref="CancellationTokenSource"/> across reads to avoid timer churn.
/// </summary>
internal sealed class PipeBoundedLineReader : IDisposable
{
    private const int DefaultBufferChars = 8 * 1024;

    private readonly StreamReader _reader;
    private char[]? _buffer;
    private int _pos;
    private int _len;
    private CancellationTokenSource? _reusableIdleCts;
    private CancellationToken _reusableParentToken;

    public PipeBoundedLineReader(
        Stream stream,
        int streamReaderBufferBytes = 16 * 1024,
        bool detectEncodingFromByteOrderMarks = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: detectEncodingFromByteOrderMarks,
            bufferSize: streamReaderBufferBytes,
            leaveOpen: true);
        _buffer = ArrayPool<char>.Shared.Rent(DefaultBufferChars);
    }

    public async ValueTask<string?> ReadLineAsync(
        int maxChars,
        CancellationToken cancellationToken,
        Action<int>? onOversizedLine = null,
        TimeSpan? idleTimeout = null)
    {
        BoundedLineReadResult result = await ReadBoundedLineAsync(maxChars, cancellationToken, idleTimeout)
            .ConfigureAwait(false);
        if (result.Kind == BoundedLineReadKind.Oversized)
        {
            onOversizedLine?.Invoke(result.ObservedLength);
            return null;
        }

        return result.Kind == BoundedLineReadKind.Line ? result.Line : null;
    }

    public async ValueTask<BoundedLineReadResult> ReadBoundedLineAsync(
        int maxChars,
        CancellationToken cancellationToken,
        TimeSpan? idleTimeout = null)
    {
        char[]? buffer = _buffer;
        ObjectDisposedException.ThrowIf(buffer is null, this);

        StringBuilder? accumulator = null;

        while (true)
        {
            if (_pos < _len)
            {
                ReadOnlySpan<char> available = buffer.AsSpan(_pos, _len - _pos);
                int newlineOffset = available.IndexOf('\n');
                if (newlineOffset >= 0)
                {
                    ReadOnlySpan<char> segment = available[..newlineOffset];
                    _pos += newlineOffset + 1;

                    if (accumulator is null)
                    {
                        if (segment.Length > 0 && segment[^1] == '\r')
                        {
                            segment = segment[..^1];
                        }

                        if (segment.Length > maxChars)
                        {
                            return BoundedLineReadResult.FromOversized(segment.Length);
                        }

                        return BoundedLineReadResult.FromLine(segment.Length == 0 ? string.Empty : new string(segment));
                    }

                    int nextLen = accumulator.Length + segment.Length;
                    if (segment.Length > 0 && segment[^1] == '\r')
                    {
                        segment = segment[..^1];
                        nextLen--;
                    }
                    else if (segment.Length == 0 && accumulator.Length > 0 && accumulator[^1] == '\r')
                    {
                        accumulator.Length--;
                        nextLen--;
                    }

                    if (nextLen > maxChars)
                    {
                        return BoundedLineReadResult.FromOversized(nextLen);
                    }

                    if (segment.Length > 0)
                    {
                        accumulator.Append(segment);
                    }

                    return BoundedLineReadResult.FromLine(accumulator.ToString());
                }

                int totalAfterChunk = (accumulator?.Length ?? 0) + available.Length;
                // Allow +1 in case a trailing '\r' precedes '\n' in the next chunk.
                if (totalAfterChunk > maxChars + 1)
                {
                    return BoundedLineReadResult.FromOversized(totalAfterChunk);
                }

                accumulator ??= new StringBuilder(Math.Min(maxChars + 1, Math.Max(256, available.Length * 2)));
                accumulator.Append(available);
                _pos = _len = 0;
            }

            int charsRead = await ReadNextChunkAsync(buffer, cancellationToken, idleTimeout).ConfigureAwait(false);
            if (charsRead <= 0)
            {
                if (accumulator is null || accumulator.Length == 0)
                {
                    return BoundedLineReadResult.Eof;
                }

                if (accumulator[^1] == '\r')
                {
                    accumulator.Length--;
                }

                if (accumulator.Length > maxChars)
                {
                    return BoundedLineReadResult.FromOversized(accumulator.Length);
                }

                return BoundedLineReadResult.FromLine(accumulator.ToString());
            }

            _pos = 0;
            _len = charsRead;
        }
    }

    private async ValueTask<int> ReadNextChunkAsync(
        char[] buffer,
        CancellationToken cancellationToken,
        TimeSpan? idleTimeout)
    {
        if (idleTimeout is not { } timeout || timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            return await _reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        CancellationTokenSource idleCts = GetOrCreateIdleCts(cancellationToken);
        idleCts.CancelAfter(timeout);
        try
        {
            return await _reader.ReadAsync(buffer.AsMemory(), idleCts.Token).ConfigureAwait(false);
        }
        finally
        {
            if (!idleCts.IsCancellationRequested)
            {
                try
                {
                    idleCts.CancelAfter(Timeout.InfiniteTimeSpan);
                }
                catch (ObjectDisposedException)
                {
                }
            }
        }
    }

    private CancellationTokenSource GetOrCreateIdleCts(CancellationToken parentToken)
    {
        if (_reusableIdleCts is not null &&
            !_reusableIdleCts.IsCancellationRequested &&
            _reusableParentToken == parentToken)
        {
            return _reusableIdleCts;
        }

        _reusableIdleCts?.Dispose();
        _reusableParentToken = parentToken;
        _reusableIdleCts = CancellationTokenSource.CreateLinkedTokenSource(parentToken);
        return _reusableIdleCts;
    }

    public void Dispose()
    {
        char[]? rented = Interlocked.Exchange(ref _buffer, null);
        if (rented is not null)
        {
            ArrayPool<char>.Shared.Return(rented);
            _reader.Dispose();
        }

        CancellationTokenSource? cts = Interlocked.Exchange(ref _reusableIdleCts, null);
        cts?.Dispose();
        _reusableParentToken = default;
    }
}

/// <summary>
/// Thread-agnostic <see cref="IBufferWriter{T}"/> backed directly by
/// <see cref="ArrayPool{T}.Shared"/> so async IPC writes across thread-pool
/// continuations never share or leak <c>[ThreadStatic]</c> buffers.
/// </summary>
internal sealed class PooledByteBufferWriter : IBufferWriter<byte>, IDisposable
{
    private byte[]? _buffer;
    private int _index;

    public PooledByteBufferWriter(int initialCapacity)
    {
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Max(256, initialCapacity));
        _index = 0;
    }

    public ReadOnlyMemory<byte> WrittenMemory =>
        _buffer is null ? ReadOnlyMemory<byte>.Empty : _buffer.AsMemory(0, _index);

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_buffer is null || _index > _buffer.Length - count)
        {
            throw new InvalidOperationException("Cannot advance past the end of the pooled buffer.");
        }

        _index += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsMemory(_index);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        EnsureCapacity(sizeHint);
        return _buffer.AsSpan(_index);
    }

    private void EnsureCapacity(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        byte[]? current = _buffer ?? throw new ObjectDisposedException(nameof(PooledByteBufferWriter));
        int required = Math.Max(sizeHint, 1);
        int available = current.Length - _index;
        if (available >= required)
        {
            return;
        }

        long doubled = (long)current.Length * 2;
        long needed = (long)_index + required;
        int nextSize = (int)Math.Clamp(Math.Max(doubled, needed), 256, Array.MaxLength);
        if (nextSize < needed)
        {
            throw new OutOfMemoryException("Requested IPC envelope exceeds maximum array capacity.");
        }

        byte[] next = ArrayPool<byte>.Shared.Rent(nextSize);
        if (_index > 0)
        {
            current.AsSpan(0, _index).CopyTo(next);
        }

        _buffer = next;
        ArrayPool<byte>.Shared.Return(current);
    }

    public void Dispose()
    {
        byte[]? rented = Interlocked.Exchange(ref _buffer, null);
        _index = 0;
        if (rented is not null)
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}

/// <summary>
/// Encodes <see cref="SettingsIpcEnvelope"/> directly into UTF-8 bytes with a
/// trailing newline without allocating intermediate <see cref="string"/> copies.
/// </summary>
internal static class PipeEnvelopeSerializer
{
    public static async ValueTask WriteEnvelopeAsync(
        Stream stream,
        Action<Utf8JsonWriter> writeJsonAction,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(writeJsonAction);
        using var writer = new PooledByteBufferWriter(1024);
        using (var jsonWriter = new Utf8JsonWriter(writer))
        {
            writeJsonAction(jsonWriter);
        }

        Span<byte> newline = writer.GetSpan(1);
        newline[0] = (byte)'\n';
        writer.Advance(1);

        await stream.WriteAsync(writer.WrittenMemory, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }
}
