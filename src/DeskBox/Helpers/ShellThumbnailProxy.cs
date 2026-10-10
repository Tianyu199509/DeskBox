using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Win32;

namespace DeskBox.Helpers;

/// <summary>
/// Resolves Explorer thumbnails and Shell-item icons in a short-lived native
/// process. No Shell handler DLL is ever loaded into the DeskBox process, and
/// a hung or crashing handler is contained by the per-request timeout.
/// Concurrently arriving requests are coalesced into one batch process
/// (see <c>--extract-batch</c> in the native proxy), so process creation,
/// COM apartment setup, and Shell handler DLL loads are paid once per batch
/// instead of once per icon. Set DESKBOX_DISABLE_ICON_BATCH=1 to fall back to
/// the legacy one-process-per-request path.
/// </summary>
internal static class ShellThumbnailProxy
{
    internal enum ShellImageMode
    {
        Thumbnail,
        Icon,
        IconWithOverlays
    }

    private readonly record struct BitmapPayloadInfo(
        int Width,
        int Height,
        int SignedHeight,
        int PixelOffset,
        int MinX,
        int MinY,
        int MaxX,
        int MaxY,
        byte PeakAlpha)
    {
        public int VisibleWidth => MaxX - MinX + 1;
        public int VisibleHeight => MaxY - MinY + 1;
    }

    internal const string ExecutableName = "DeskBox.ThumbnailProxy.exe";
    private const string ThumbnailHandlerClassId =
        "{e357fccd-a995-4576-b01f-234630154e96}";
    private const int MaximumPayloadBytes = 2 * 1024 * 1024;
    private const int MaximumFailureEntries = 256;
    private const long FailureRetryDelayMilliseconds = 30_000;
    private static readonly TimeSpan ExtractionTimeout =
        TimeSpan.FromMilliseconds(2500);
    private const uint BatchProtocolMagic = 0x4458_4231;
    private const uint BatchProtocolVersion = 1;
    // One batch process runs at a time (the dispatcher drains the queue
    // sequentially), so a larger chunk only amortizes process creation and
    // Shell handler DLL loads. The native proxy caps its own worker fan-out
    // independently (feedback 226/452/495), so this stays above that cap on
    // purpose.
    private const int MaximumBatchRequests = 8;
    private static readonly TimeSpan BatchCoalesceWindow =
        TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan BatchExtractionTimeout =
        TimeSpan.FromMilliseconds(2500);
    // Safety net so a caller can never hang on a lost or crashed dispatcher;
    // every batch normally resolves within BatchExtractionTimeout.
    private static readonly TimeSpan BatchWaiterSafetyTimeout =
        TimeSpan.FromMilliseconds(6000);
    private static readonly bool s_batchExtractionDisabled =
        Environment.GetEnvironmentVariable("DESKBOX_DISABLE_ICON_BATCH") == "1";
    private static readonly ConcurrentQueue<ProxyBatchRequest> s_batchQueue = new();
    private static int s_batchDispatchScheduled;
    private static readonly ConcurrentDictionary<string, bool>
        s_registeredProviderByExtension = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, long>
        s_recentFailures = new(StringComparer.OrdinalIgnoreCase);
    private static int s_missingExecutableLogged;

    internal sealed record ProxyBatchRequest(
        ShellImageMode Mode,
        string Path,
        int Size,
        TaskCompletionSource<byte[]?> Completion);

    public static Task<bool> HasRegisteredThumbnailProviderAsync(string path)
    {
        string extension = Path.GetExtension(path);
        if (string.IsNullOrWhiteSpace(extension) ||
            IsExcludedExtension(extension))
        {
            return Task.FromResult(false);
        }

        if (s_registeredProviderByExtension.TryGetValue(
                extension,
                out bool cached))
        {
            return Task.FromResult(cached);
        }

        return Task.Run(() => s_registeredProviderByExtension.GetOrAdd(
            extension,
            QueryRegisteredThumbnailProvider));
    }

    public static async Task<byte[]?> TryLoadAsync(
        string path,
        int requestedSize)
    {
        return await TryLoadAsync(path, requestedSize, ShellImageMode.Thumbnail);
    }

    public static async Task<byte[]?> TryLoadIconAsync(
        string path,
        int requestedSize,
        bool includeOverlays = false)
    {
        return await TryLoadAsync(
            path,
            requestedSize,
            includeOverlays
                ? ShellImageMode.IconWithOverlays
                : ShellImageMode.Icon);
    }

    /// <summary>
    /// Loads a Shell-item icon without cropping a padded canvas, so the caller
    /// can decide whether a later request would return an icon frame the canvas
    /// actually fills. <see cref="TryLoadIconAsync"/> keeps cropping for every
    /// caller that only needs a usable bitmap.
    /// </summary>
    public static async Task<byte[]?> TryLoadIconPayloadAsync(
        string path,
        int requestedSize)
    {
        return await TryLoadAsync(
            path,
            requestedSize,
            ShellImageMode.Icon,
            normalizeIconPayload: false);
    }

    private static async Task<byte[]?> TryLoadAsync(
        string path,
        int requestedSize,
        ShellImageMode mode,
        bool normalizeIconPayload = true)
    {
        string normalizedPath = NormalizePath(path);
        string failureKey = BuildFailureKey(normalizedPath, mode);
        if (IsRecentFailure(failureKey))
        {
            return null;
        }

        string executablePath = Path.Combine(
            AppContext.BaseDirectory,
            ExecutableName);
        if (!File.Exists(executablePath))
        {
            if (Interlocked.Exchange(ref s_missingExecutableLogged, 1) == 0)
            {
                App.Log(
                    $"[ShellThumbnailProxy] Native proxy is missing: " +
                    $"{executablePath}");
            }

            RecordFailure(failureKey);
            return null;
        }

        int normalizedSize = Math.Clamp(requestedSize, 24, 512);
        byte[]? output = s_batchExtractionDisabled
            ? await ExtractViaSingleProcessAsync(
                executablePath,
                normalizedPath,
                normalizedSize,
                mode)
            : await ExtractViaBatchAsync(
                executablePath,
                normalizedPath,
                normalizedSize,
                mode);

        if (output is null || !IsVisibleBitmapPayload(output))
        {
            RecordFailure(failureKey);
            App.LogVerbose(
                $"[ShellThumbnailProxy] No usable image mode={mode} " +
                $"path={normalizedPath}");
            return null;
        }

        if (normalizeIconPayload &&
            mode is ShellImageMode.Icon or ShellImageMode.IconWithOverlays)
        {
            byte[]? normalizedOutput = NormalizeIconPayload(output);
            if (normalizedOutput is null)
            {
                RecordFailure(failureKey);
                App.LogVerbose(
                    $"[ShellThumbnailProxy] Unable to normalize Shell-item icon " +
                    $"path={normalizedPath}");
                return null;
            }

            if (normalizedOutput.Length != output.Length)
            {
                App.LogVerbose(
                    $"[ShellThumbnailProxy] Cropped padded Shell-item icon " +
                    $"path={normalizedPath}");
            }

            output = normalizedOutput;
        }

        s_recentFailures.TryRemove(failureKey, out _);
        return output;
    }

    /// <summary>
    /// Legacy single-request path: one proxy process per extraction. Kept as
    /// the fallback for <c>DESKBOX_DISABLE_ICON_BATCH=1</c> and for A/B
    /// comparison against the batched dispatcher.
    /// </summary>
    private static async Task<byte[]?> ExtractViaSingleProcessAsync(
        string executablePath,
        string normalizedPath,
        int normalizedSize,
        ShellImageMode mode)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        if (mode is ShellImageMode.Icon or ShellImageMode.IconWithOverlays)
        {
            startInfo.ArgumentList.Add(
                mode == ShellImageMode.IconWithOverlays
                    ? "--icon-with-overlays"
                    : "--icon-only");
        }

        startInfo.ArgumentList.Add(normalizedPath);
        startInfo.ArgumentList.Add(normalizedSize.ToString(
            System.Globalization.CultureInfo.InvariantCulture));

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            App.Log(
                $"[ShellThumbnailProxy] Start failed mode={mode} " +
                $"path={normalizedPath}: " +
                ex.Message);
            return null;
        }

        Task<byte[]> outputTask = ReadBoundedOutputAsync(
            process.StandardOutput.BaseStream,
            MaximumPayloadBytes);
        Task<string> errorTask = process.StandardError.ReadToEndAsync();
        using var timeoutSource = new CancellationTokenSource(
            ExtractionTimeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            await ObserveOutputAsync(outputTask, errorTask);
            App.Log(
                $"[ShellThumbnailProxy] Extraction timed out " +
                $"timeoutMs={ExtractionTimeout.TotalMilliseconds:0} " +
                $"mode={mode} path={normalizedPath}");
            return null;
        }

        byte[] output;
        string error;
        try
        {
            output = await outputTask;
            error = await errorTask;
        }
        catch (Exception ex)
        {
            TryKill(process);
            App.Log(
                $"[ShellThumbnailProxy] Invalid proxy output " +
                $"mode={mode} path={normalizedPath}: {ex.Message}");
            return null;
        }

        if (process.ExitCode != 0)
        {
            App.LogVerbose(
                $"[ShellThumbnailProxy] Proxy exited {process.ExitCode} " +
                $"mode={mode} path={normalizedPath} error={error.Trim()}");
            return null;
        }

        return output;
    }

    /// <summary>
    /// Queues one extraction for batch dispatch. Icon hydration fires its
    /// items concurrently, so requests arriving within the coalesce window
    /// share a single proxy process instead of each paying process creation,
    /// COM apartment setup, and Shell handler DLL loads.
    /// </summary>
    private static async Task<byte[]?> ExtractViaBatchAsync(
        string executablePath,
        string normalizedPath,
        int normalizedSize,
        ShellImageMode mode)
    {
        var completion = new TaskCompletionSource<byte[]?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        s_batchQueue.Enqueue(
            new ProxyBatchRequest(mode, normalizedPath, normalizedSize, completion));
        ScheduleBatchDispatch(executablePath);
        try
        {
            return await completion.Task.WaitAsync(BatchWaiterSafetyTimeout);
        }
        catch (TimeoutException)
        {
            return null;
        }
    }

    private static void ScheduleBatchDispatch(string executablePath)
    {
        if (Interlocked.Exchange(ref s_batchDispatchScheduled, 1) != 0)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(BatchCoalesceWindow);
                var batch = new List<ProxyBatchRequest>(MaximumBatchRequests);
                while (true)
                {
                    batch.Clear();
                    while (batch.Count < MaximumBatchRequests &&
                        s_batchQueue.TryDequeue(out ProxyBatchRequest? request))
                    {
                        batch.Add(request);
                    }

                    if (batch.Count == 0)
                    {
                        break;
                    }

                    await DispatchBatchAsync(executablePath, batch);
                }
            }
            catch (Exception ex)
            {
                App.Log($"[ShellThumbnailProxy] Batch dispatcher failed: {ex.Message}");
            }
            finally
            {
                Volatile.Write(ref s_batchDispatchScheduled, 0);
                if (!s_batchQueue.IsEmpty)
                {
                    ScheduleBatchDispatch(executablePath);
                }
            }
        });
    }

    private static async Task DispatchBatchAsync(
        string executablePath,
        List<ProxyBatchRequest> batch)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = AppContext.BaseDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("--extract-batch");

        using var process = new Process { StartInfo = startInfo };
        var fulfilled = new bool[batch.Count];
        Task<string>? errorTask = null;
        try
        {
            if (!process.Start())
            {
                throw new InvalidOperationException(
                    "The thumbnail proxy process failed to start.");
            }

            errorTask = process.StandardError.ReadToEndAsync();
            WriteBatchManifest(process.StandardInput.BaseStream, batch);
            using var timeoutSource = new CancellationTokenSource(
                BatchExtractionTimeout);
            int failedCount = await ReadBatchFramesAsync(
                process.StandardOutput.BaseStream,
                batch,
                fulfilled,
                timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token);
            if (failedCount > 0)
            {
                // Per-item extraction failures are otherwise silent at the
                // default log level, which hid a whole class of "icon never
                // loads" regressions during cold startup (2026-09-14).
                App.Log(
                    $"[ShellThumbnailProxy] Batch items failed " +
                    $"count={failedCount}/{batch.Count}");
            }
        }
        catch (Exception ex)
        {
            TryKill(process);
            await ObserveExitAsync(process);
            if (errorTask is not null)
            {
                try
                {
                    _ = await errorTask;
                }
                catch
                {
                }
            }

            App.Log(
                $"[ShellThumbnailProxy] Batch failed count={batch.Count}: {ex.Message}");
        }
        finally
        {
            for (int index = 0; index < batch.Count; index++)
            {
                if (!fulfilled[index])
                {
                    batch[index].Completion.TrySetResult(null);
                }
            }
        }
    }

    /// <summary>
    /// Writes the count-prefixed batch manifest and closes the stream: the
    /// count-prefixed proxy reads exactly this many requests, and closing
    /// stdin signals no more input follows. The writer owns the close (an
    /// explicit stream close before writer disposal throws
    /// ObjectDisposedException from the writer's final flush).
    /// </summary>
    internal static void WriteBatchManifest(
        Stream stream,
        IReadOnlyList<ProxyBatchRequest> batch)
    {
        using var writer = new System.IO.BinaryWriter(
            stream,
            System.Text.Encoding.UTF8,
            leaveOpen: false);
        writer.Write(BatchProtocolMagic);
        writer.Write(BatchProtocolVersion);
        writer.Write((uint)batch.Count);
        foreach (ProxyBatchRequest request in batch)
        {
            writer.Write(BatchModeValue(request.Mode));
            writer.Write((uint)request.Size);
            byte[] pathBytes = System.Text.Encoding.Unicode.GetBytes(request.Path);
            writer.Write((uint)pathBytes.Length);
            writer.Write(pathBytes);
        }

        writer.Flush();
    }

    private static uint BatchModeValue(ShellImageMode mode) => mode switch
    {
        ShellImageMode.Thumbnail => 0,
        ShellImageMode.Icon => 1,
        _ => 2,
    };

    /// <summary>
    /// Reads result frames from a batch proxy until every request resolved,
    /// the proxy exits early, or the caller's cancellation kills the batch.
    /// Frames may arrive in any order; each request completes as soon as its
    /// frame lands, so one hung extraction cannot hold its batch-mates back.
    /// Reports how many frames carried an extraction failure.
    /// </summary>
    internal static async Task<int> ReadBatchFramesAsync(
        Stream stream,
        IReadOnlyList<ProxyBatchRequest> batch,
        bool[] fulfilled,
        CancellationToken cancellationToken)
    {
        byte[] header = new byte[12];
        int remaining = batch.Count;
        int failedCount = 0;
        while (remaining > 0)
        {
            int headerBytes = await stream.ReadAtLeastAsync(
                header,
                header.Length,
                throwOnEndOfStream: false,
                cancellationToken).ConfigureAwait(false);
            if (headerBytes == 0)
            {
                // The proxy exited before writing every frame; the dispatcher
                // fails the requests that never received one.
                break;
            }

            if (headerBytes != header.Length)
            {
                throw new InvalidDataException(
                    "The thumbnail proxy returned a truncated batch frame header.");
            }

            uint index = BitConverter.ToUInt32(header, 0);
            uint status = BitConverter.ToUInt32(header, 4);
            uint length = BitConverter.ToUInt32(header, 8);
            if (index >= (uint)batch.Count || fulfilled[index])
            {
                throw new InvalidDataException(
                    $"The thumbnail proxy returned an invalid batch frame index {index}.");
            }

            if (status == 0 && (length < 138 || length > MaximumPayloadBytes))
            {
                throw new InvalidDataException(
                    "The thumbnail proxy batch payload size was outside its limit.");
            }

            byte[] payload = new byte[length];
            if (length > 0)
            {
                await stream.ReadExactlyAsync(payload, cancellationToken)
                    .ConfigureAwait(false);
            }

            fulfilled[index] = true;
            remaining--;
            if (status == 0)
            {
                batch[(int)index].Completion.TrySetResult(payload);
            }
            else
            {
                failedCount++;
                batch[(int)index].Completion.TrySetResult(null);
            }
        }

        return failedCount;
    }

    public static void Invalidate(string path)
    {
        string normalizedPath = NormalizePath(path);
        s_recentFailures.TryRemove(
            BuildFailureKey(normalizedPath, ShellImageMode.Thumbnail),
            out _);
        s_recentFailures.TryRemove(
            BuildFailureKey(normalizedPath, ShellImageMode.Icon),
            out _);
        s_recentFailures.TryRemove(
            BuildFailureKey(normalizedPath, ShellImageMode.IconWithOverlays),
            out _);
    }

    public static void ClearTransientFailures()
    {
        s_recentFailures.Clear();
    }

    private static bool QueryRegisteredThumbnailProvider(string extension)
    {
        try
        {
            using RegistryKey? extensionKey =
                Registry.ClassesRoot.OpenSubKey(extension);
            string? programmaticId = extensionKey?.GetValue(null) as string;
            string? perceivedType = extensionKey?.GetValue(
                "PerceivedType") as string;

            return HasHandler(extension) ||
                HasHandler($"SystemFileAssociations\\{extension}") ||
                (!string.IsNullOrWhiteSpace(programmaticId) &&
                 HasHandler(programmaticId)) ||
                (!string.IsNullOrWhiteSpace(perceivedType) &&
                 HasHandler($"SystemFileAssociations\\{perceivedType}"));
        }
        catch
        {
            return false;
        }
    }

    private static bool HasHandler(string classPath)
    {
        using RegistryKey? key = Registry.ClassesRoot.OpenSubKey(
            $"{classPath}\\ShellEx\\{ThumbnailHandlerClassId}");
        return key?.GetValue(null) is string value &&
            !string.IsNullOrWhiteSpace(value);
    }

    private static bool IsExcludedExtension(string extension) =>
        extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".dll", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".ico", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
        extension.Equals(".url", StringComparison.OrdinalIgnoreCase);

    internal static async Task<byte[]> ReadBoundedOutputAsync(
        Stream stream,
        int maximumBytes)
    {
        // The native proxy emits one BMP with its total length in the file
        // header. Read directly into the final array to avoid MemoryStream's
        // growth buffers and the additional full-size ToArray copy.
        byte[] header = new byte[14];
        int headerBytes = await stream.ReadAtLeastAsync(
            header,
            header.Length,
            throwOnEndOfStream: false).ConfigureAwait(false);
        if (headerBytes == 0)
        {
            return [];
        }

        if (headerBytes != header.Length ||
            header[0] != (byte)'B' || header[1] != (byte)'M')
        {
            throw new InvalidDataException(
                "The thumbnail proxy returned an invalid bitmap header.");
        }

        uint declaredSize = BitConverter.ToUInt32(header, 2);
        if (declaredSize < 138 || declaredSize > maximumBytes)
        {
            throw new InvalidDataException(
                "The thumbnail proxy payload size was outside its limit.");
        }

        byte[] output = new byte[(int)declaredSize];
        header.CopyTo(output, 0);
        await stream.ReadExactlyAsync(
            output.AsMemory(header.Length)).ConfigureAwait(false);
        if (await stream.ReadAsync(header.AsMemory(0, 1)).ConfigureAwait(false) != 0)
        {
            throw new InvalidDataException(
                "The thumbnail proxy returned data beyond its bitmap payload.");
        }

        return output;
    }

    internal static bool IsVisibleBitmapPayload(byte[] bytes)
    {
        return TryReadVisibleBitmapPayload(bytes, out _);
    }

    internal static bool IsLikelyPaddedIconPayload(byte[] bytes)
    {
        if (!TryReadVisibleBitmapPayload(
                bytes,
                out BitmapPayloadInfo payload))
        {
            return false;
        }

        BitmapPayloadInfo artwork = ResolveArtworkBounds(bytes, payload);
        return IconBitmapQuality.IsLikelyPadded(
            payload.Width,
            payload.Height,
            artwork.VisibleWidth,
            artwork.VisibleHeight);
    }

    /// <summary>
    /// Resolves the bounds that read as visible artwork. Pixels below
    /// <see cref="IconBitmapQuality.SignificantAlphaThreshold"/> are ignored so
    /// the near-invisible border the Shell paints around a small icon frame it
    /// centered inside a larger canvas cannot make a padded canvas look full.
    /// Payloads whose every pixel is that faint keep their non-transparent
    /// bounds, which preserves the pre-existing behavior for them.
    /// </summary>
    private static BitmapPayloadInfo ResolveArtworkBounds(
        byte[] bytes,
        BitmapPayloadInfo payload)
    {
        byte threshold = IconBitmapQuality.SignificantAlphaThreshold(
            payload.PeakAlpha);
        if (payload.PeakAlpha < threshold)
        {
            return payload;
        }

        int rowByteCount = checked(payload.Width * 4);
        int minX = payload.Width;
        int minY = payload.Height;
        int maxX = -1;
        int maxY = -1;
        for (int y = 0; y < payload.Height; y++)
        {
            int rowStart = checked(payload.PixelOffset + (y * rowByteCount));
            for (int x = 0; x < payload.Width; x++)
            {
                if (bytes[rowStart + (x * 4) + 3] < threshold)
                {
                    continue;
                }

                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return payload;
        }

        return payload with
        {
            MinX = minX,
            MinY = minY,
            MaxX = maxX,
            MaxY = maxY
        };
    }

    /// <summary>
    /// Crops the transparent border from a Shell icon only when the visible
    /// artwork is clearly a small glyph inside a Jumbo canvas. The normalized
    /// payload remains a 32-bit top-down BMP so the caller can decode it through
    /// the same BitmapImage path as an unmodified proxy result.
    /// </summary>
    internal static byte[]? NormalizeIconPayload(byte[] bytes)
    {
        if (!TryReadVisibleBitmapPayload(
                bytes,
                out BitmapPayloadInfo payload))
        {
            return null;
        }

        BitmapPayloadInfo artwork = ResolveArtworkBounds(bytes, payload);
        if (!IconBitmapQuality.IsLikelyPadded(
                payload.Width,
                payload.Height,
                artwork.VisibleWidth,
                artwork.VisibleHeight))
        {
            return bytes;
        }

        try
        {
            int cropWidth = artwork.VisibleWidth;
            int cropHeight = artwork.VisibleHeight;
            int rowByteCount = checked(cropWidth * 4);
            int pixelByteCount = checked(rowByteCount * cropHeight);
            int outputSize = checked(payload.PixelOffset + pixelByteCount);
            byte[] normalized = new byte[outputSize];
            Buffer.BlockCopy(
                bytes,
                0,
                normalized,
                0,
                payload.PixelOffset);
            WriteInt32(normalized, 2, outputSize);
            WriteInt32(normalized, 18, cropWidth);
            WriteInt32(normalized, 22, -cropHeight);
            WriteInt32(normalized, 34, pixelByteCount);

            int sourceRowByteCount = checked(payload.Width * 4);
            for (int y = 0; y < cropHeight; y++)
            {
                int sourceY = payload.SignedHeight < 0
                    ? artwork.MinY + y
                    : artwork.MaxY - y;
                int sourceOffset = checked(
                    payload.PixelOffset +
                    (sourceY * sourceRowByteCount) +
                    (artwork.MinX * 4));
                int destinationOffset = checked(
                    payload.PixelOffset + (y * rowByteCount));
                Buffer.BlockCopy(
                    bytes,
                    sourceOffset,
                    normalized,
                    destinationOffset,
                    rowByteCount);
            }

            return normalized;
        }
        catch (Exception ex)
        {
            App.LogVerbose(
                $"[ShellThumbnailProxy] Icon payload crop failed: {ex.Message}");
            return null;
        }
    }

    private static bool TryReadVisibleBitmapPayload(
        byte[] bytes,
        out BitmapPayloadInfo payload)
    {
        payload = default;
        if (bytes.Length < 138 || bytes[0] != (byte)'B' || bytes[1] != (byte)'M')
        {
            return false;
        }

        uint declaredSize = BitConverter.ToUInt32(bytes, 2);
        uint pixelOffset = BitConverter.ToUInt32(bytes, 10);
        uint dibHeaderSize = BitConverter.ToUInt32(bytes, 14);
        int signedHeight = BitConverter.ToInt32(bytes, 22);
        ushort bitsPerPixel = BitConverter.ToUInt16(bytes, 28);
        long absoluteHeight = Math.Abs((long)signedHeight);
        int parsedWidth = BitConverter.ToInt32(bytes, 18);
        if (declaredSize != bytes.Length ||
            dibHeaderSize < 40 ||
            parsedWidth <= 0 ||
            absoluteHeight <= 0 ||
            absoluteHeight > int.MaxValue ||
            bitsPerPixel != 32 ||
            pixelOffset < 54 ||
            pixelOffset >= bytes.Length)
        {
            return false;
        }

        long pixelByteCount = (long)parsedWidth * absoluteHeight * 4;
        if (pixelByteCount > bytes.Length - pixelOffset)
        {
            return false;
        }

        int pixelStart = checked((int)pixelOffset);
        int rowByteCount = checked(parsedWidth * 4);
        int parsedHeight = (int)absoluteHeight;
        int minX = parsedWidth;
        int minY = parsedHeight;
        int maxX = -1;
        int maxY = -1;
        byte peakAlpha = 0;
        for (int y = 0; y < parsedHeight; y++)
        {
            int rowStart = checked(pixelStart + (y * rowByteCount));
            for (int x = 0; x < parsedWidth; x++)
            {
                byte alpha = bytes[rowStart + (x * 4) + 3];
                if (alpha == 0)
                {
                    continue;
                }

                peakAlpha = Math.Max(peakAlpha, alpha);
                minX = Math.Min(minX, x);
                maxX = Math.Max(maxX, x);
                minY = Math.Min(minY, y);
                maxY = Math.Max(maxY, y);
            }
        }

        if (maxX < minX || maxY < minY)
        {
            return false;
        }

        payload = new BitmapPayloadInfo(
            parsedWidth,
            parsedHeight,
            signedHeight,
            checked((int)pixelOffset),
            minX,
            minY,
            maxX,
            maxY,
            peakAlpha);
        return true;
    }

    private static void WriteInt32(byte[] bytes, int offset, int value)
    {
        BitConverter.GetBytes(value).CopyTo(bytes, offset);
    }

    private static string BuildFailureKey(
        string normalizedPath,
        ShellImageMode mode) => $"{mode}:{normalizedPath}";

    private static bool IsRecentFailure(string path)
    {
        if (!s_recentFailures.TryGetValue(path, out long failedAt))
        {
            return false;
        }

        if (Environment.TickCount64 - failedAt < FailureRetryDelayMilliseconds)
        {
            return true;
        }

        s_recentFailures.TryRemove(path, out _);
        return false;
    }

    private static void RecordFailure(string path)
    {
        if (s_recentFailures.Count >= MaximumFailureEntries &&
            !s_recentFailures.ContainsKey(path))
        {
            s_recentFailures.Clear();
        }

        s_recentFailures[path] = Environment.TickCount64;
    }

    private static string NormalizePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static async Task ObserveExitAsync(Process process)
    {
        try
        {
            await process.WaitForExitAsync();
        }
        catch
        {
        }
    }

    private static async Task ObserveOutputAsync(
        Task<byte[]> outputTask,
        Task<string> errorTask)
    {
        try
        {
            await Task.WhenAll(outputTask, errorTask);
        }
        catch
        {
        }
    }
}
