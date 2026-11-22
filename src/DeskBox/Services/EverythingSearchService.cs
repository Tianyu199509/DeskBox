using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using DeskBox.Models;
using DeskBox.Contracts;
using DeskBox.Platform;

namespace DeskBox.Services;

internal sealed record SearchFileQueryPage(
    IReadOnlyList<SearchResultItem> Items,
    int TotalMatchedCount,
    int NextOffset)
{
    public static SearchFileQueryPage Empty { get; } = new([], 0, 0);
}

/// <summary>
/// The only DeskBox filename provider. It queries the official Everything SDK over
/// local IPC and never creates, scans, watches, or persists a second filename index.
/// </summary>
public sealed class EverythingSearchService : IDisposable, ISearchConnectionClient
{
    private const int MaximumInitialListCapacity = 4_000;
    private const int SupplementaryTopHitBatchSize = 100;
    private const long ConnectedProbeTtlMilliseconds = 30_000;
    private const long FailedProbeTtlMilliseconds = 2_000;

    private readonly SettingsService _settingsService;
    private readonly SemaphoreSlim _nativeGate = new(1, 1);
    private readonly object _snapshotLock = new();
    private EverythingConnectionSnapshot _snapshot = EverythingConnectionSnapshot.Unknown;
    private EverythingInstallationSnapshot? _lastInstallation;
    private long _lastProbeTick = long.MinValue;
    private bool _isDisposed;

    public EverythingSearchService(SettingsService settingsService)
    {
        _settingsService = settingsService;
    }

    public event Action<EverythingConnectionSnapshot>? ConnectionChanged;

    public EverythingConnectionSnapshot CurrentSnapshot
    {
        get
        {
            lock (_snapshotLock)
            {
                return _snapshot;
            }
        }
    }

    /// <summary>
    /// Detects the executable and process state. IPC is attempted only after the user
    /// has enabled Everything integration and the caller permits a connection probe.
    /// </summary>
    public async Task<EverythingConnectionSnapshot> RefreshConnectionAsync(
        bool allowIpcProbe = true,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        PublishSnapshot(CurrentSnapshot with
        {
            State = EverythingConnectionState.Checking,
            DiagnosticCode = null
        });

        string configuredPath = _settingsService.Settings.SearchEverythingExecutablePath;
        EverythingInstallationSnapshot installation = await Task.Run(
            () => EverythingInstallationDetector.Detect(configuredPath),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _lastInstallation = installation;

        if (string.IsNullOrWhiteSpace(installation.ExecutablePath))
        {
            return PublishSnapshot(CreateSnapshot(
                EverythingConnectionState.NotInstalled,
                installation,
                "executable-not-found"));
        }

        if (!_settingsService.Settings.SearchEverythingEnabled || !allowIpcProbe)
        {
            return PublishSnapshot(CreateSnapshot(
                EverythingConnectionState.NotConfirmed,
                installation,
                "consent-required"));
        }

        if (!installation.IsRunning)
        {
            return PublishSnapshot(CreateSnapshot(
                EverythingConnectionState.NotRunning,
                installation,
                "process-not-running"));
        }

        try
        {
            await _nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                (bool connected, string? version, uint error) = await Task.Run(
                    ProbeNativeConnection,
                    cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Interlocked.Exchange(ref _lastProbeTick, Environment.TickCount64);
                if (!connected)
                {
                    return PublishSnapshot(ClassifyIpcFailure(installation, error));
                }

                return PublishSnapshot(new EverythingConnectionSnapshot(
                    EverythingConnectionState.Connected,
                    installation.ExecutablePath,
                    version ?? installation.Version,
                    true,
                    installation.UsesManualPath,
                    null));
            }
            finally
            {
                _nativeGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (DllNotFoundException ex)
        {
            App.Log($"[Everything] SDK wrapper is missing: {ex.Message}");
            return PublishSnapshot(CreateSnapshot(
                EverythingConnectionState.SdkUnavailable,
                installation,
                "sdk-missing"));
        }
        catch (BadImageFormatException ex)
        {
            App.Log($"[Everything] SDK architecture mismatch: {ex.Message}");
            return PublishSnapshot(CreateSnapshot(
                EverythingConnectionState.SdkUnavailable,
                installation,
                "sdk-architecture-mismatch"));
        }
        catch (EntryPointNotFoundException ex)
        {
            App.Log($"[Everything] SDK export is unavailable: {ex.Message}");
            return PublishSnapshot(CreateSnapshot(
                EverythingConnectionState.SdkUnavailable,
                installation,
                "sdk-export-missing"));
        }
        catch (Exception ex)
        {
            App.Log($"[Everything] Connection probe failed: {ex}");
            return PublishSnapshot(CreateSnapshot(
                EverythingConnectionState.Error,
                installation,
                "probe-failed"));
        }
    }

    internal async Task<SearchFileQueryPage> SearchPageAsync(
        string query,
        int resultOffset,
        int requestedResults,
        CancellationToken cancellationToken = default)
    {
        if (_isDisposed || string.IsNullOrWhiteSpace(query))
        {
            return SearchFileQueryPage.Empty;
        }

        if (!_settingsService.Settings.SearchEverythingEnabled)
        {
            await RefreshConnectionAsync(
                allowIpcProbe: false,
                cancellationToken).ConfigureAwait(false);
            return SearchFileQueryPage.Empty;
        }

        EverythingConnectionSnapshot connection = await EnsureConnectionForQueryAsync(
            cancellationToken).ConfigureAwait(false);
        if (connection.State != EverythingConnectionState.Connected)
        {
            return SearchFileQueryPage.Empty;
        }

        int offset = Math.Max(0, resultOffset);
        int resultCount = Math.Max(1, requestedResults);
        bool advancedSyntax = _settingsService.Settings.SearchEverythingAdvancedSyntaxEnabled;
        string providerQuery = BuildProviderQuery(query, advancedSyntax);
        if (string.IsNullOrWhiteSpace(providerQuery))
        {
            return SearchFileQueryPage.Empty;
        }

        await _nativeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SearchFileQueryPage page = await Task.Run(
                () => QueryNative(
                    providerQuery,
                    query.Trim(),
                    offset,
                    resultCount,
                    cancellationToken,
                    advancedSyntax),
                cancellationToken).ConfigureAwait(false);
            return page;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EverythingIpcException ex)
        {
            EverythingInstallationSnapshot installation = _lastInstallation ??
                await Task.Run(
                    () => EverythingInstallationDetector.Detect(
                        _settingsService.Settings.SearchEverythingExecutablePath),
                    CancellationToken.None).ConfigureAwait(false);
            _lastInstallation = installation;
            PublishSnapshot(ClassifyIpcFailure(installation, ex.ErrorCode));
            App.Log($"[Everything] Query IPC failed with error {ex.ErrorCode}.");
            return SearchFileQueryPage.Empty;
        }
        catch (Exception ex) when (
            ex is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        {
            EverythingInstallationSnapshot installation = _lastInstallation ??
                EverythingInstallationDetector.Detect(
                    _settingsService.Settings.SearchEverythingExecutablePath);
            PublishSnapshot(CreateSnapshot(
                EverythingConnectionState.SdkUnavailable,
                installation,
                "sdk-unavailable"));
            App.Log($"[Everything] SDK query failed: {ex.Message}");
            return SearchFileQueryPage.Empty;
        }
        finally
        {
            _nativeGate.Release();
        }
    }

    public async Task<bool> SetExecutablePathAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        if (!EverythingInstallationDetector.IsValidExecutablePath(path))
        {
            return false;
        }

        _settingsService.Settings.SearchEverythingExecutablePath = Path.GetFullPath(path);
        _settingsService.SaveDebounced();
        await RefreshConnectionAsync(
            allowIpcProbe: _settingsService.Settings.SearchEverythingEnabled,
            cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task UseAutomaticDetectionAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _settingsService.Settings.SearchEverythingExecutablePath = string.Empty;
        _settingsService.SaveDebounced();
        await RefreshConnectionAsync(
            allowIpcProbe: _settingsService.Settings.SearchEverythingEnabled,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> LaunchEverythingAsync(
        CancellationToken cancellationToken = default)
    {
        EverythingInstallationSnapshot installation = await Task.Run(
            () => EverythingInstallationDetector.Detect(
                _settingsService.Settings.SearchEverythingExecutablePath),
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(_isDisposed, this);
        _lastInstallation = installation;
        if (!EverythingInstallationDetector.IsValidExecutablePath(installation.ExecutablePath))
        {
            PublishSnapshot(CreateSnapshot(
                EverythingConnectionState.NotInstalled,
                installation,
                "executable-not-found"));
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = installation.ExecutablePath,
                WorkingDirectory = Path.GetDirectoryName(installation.ExecutablePath),
                UseShellExecute = true
            });
            await Task.Delay(TimeSpan.FromMilliseconds(800), cancellationToken).ConfigureAwait(false);
            EverythingConnectionSnapshot snapshot = await RefreshConnectionAsync(
                allowIpcProbe: _settingsService.Settings.SearchEverythingEnabled,
                cancellationToken).ConfigureAwait(false);
            return snapshot.IsRunning;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            App.Log($"[Everything] Failed to launch '{installation.ExecutablePath}': {ex.Message}");
            return false;
        }
    }

    internal static string BuildProviderQuery(string query, bool advancedSyntax)
    {
        string normalized = query.Trim();
        if (advancedSyntax || normalized.Length == 0)
        {
            return normalized;
        }

        // Everything uses quotes to make operators (|, !, <, >) literal within each term,
        // while whitespace-separated terms outside quotes act as multi-keyword AND filters.
        // Explicit user-quoted phrases (including Windows "Copy as path" strings) stay intact
        // as single quoted terms instead of double-escaping surrounding quotes.
        IReadOnlyList<string> tokens = ExtractLiteralTokens(normalized);
        if (tokens.Count == 0)
        {
            return string.Empty;
        }

        if (tokens.Count == 1)
        {
            return $"\"{tokens[0]}\"";
        }

        var sb = new System.Text.StringBuilder(normalized.Length + (tokens.Count * 3));
        for (int i = 0; i < tokens.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append('"');
            sb.Append(tokens[i]);
            sb.Append('"');
        }

        return sb.ToString();
    }

    internal static IReadOnlyList<string> ExtractLiteralTokens(string query)
    {
        string normalized = query.Trim();
        if (normalized.Length == 0)
        {
            return [];
        }

        var tokens = new List<string>(4);
        int i = 0;
        while (i < normalized.Length)
        {
            while (i < normalized.Length && char.IsWhiteSpace(normalized[i]))
            {
                i++;
            }

            if (i >= normalized.Length)
            {
                break;
            }

            if (normalized[i] == '"')
            {
                i++;
                int start = i;
                while (i < normalized.Length && normalized[i] != '"')
                {
                    i++;
                }

                ReadOnlySpan<char> quoted = normalized.AsSpan(start, i - start);
                if (!quoted.IsWhiteSpace())
                {
                    tokens.Add(quoted.ToString());
                }

                if (i < normalized.Length && normalized[i] == '"')
                {
                    i++;
                }
            }
            else
            {
                int start = i;
                while (i < normalized.Length && !char.IsWhiteSpace(normalized[i]) && normalized[i] != '"')
                {
                    i++;
                }

                if (i > start)
                {
                    tokens.Add(normalized.Substring(start, i - start));
                }
            }
        }

        return tokens;
    }

    internal static string? BuildSupplementaryPrefixQuery(string rankingQuery)
    {
        if (HasPathSeparator(rankingQuery))
        {
            return null;
        }

        IReadOnlyList<string> tokens = ExtractLiteralTokens(rankingQuery);
        if (tokens.Count == 0)
        {
            return null;
        }

        string first = tokens[0];
        if (first.Length < 2 || first.IndexOfAny(['*', '?']) >= 0)
        {
            return null;
        }

        if (tokens.Count == 1)
        {
            return $"\"{first}*\"";
        }

        var sb = new System.Text.StringBuilder(rankingQuery.Length + (tokens.Count * 3) + 1);
        sb.Append('"').Append(first).Append("*\"");
        for (int i = 1; i < tokens.Count; i++)
        {
            sb.Append(" \"").Append(tokens[i]).Append('"');
        }

        return sb.ToString();
    }

    internal static double ComputeRelevance(string fileName, string query, string? fullPath = null)
    {
        if (string.IsNullOrEmpty(fileName) || string.IsNullOrWhiteSpace(query))
        {
            return 10;
        }

        ReadOnlySpan<char> normalizedSpan = UnwrapSingleOuterQuotes(query.AsSpan().Trim());
        if (normalizedSpan.IsEmpty || normalizedSpan.IsWhiteSpace())
        {
            return 10;
        }

        var prepared = new PreparedRankingQuery(query, normalizedSpan);
        return ComputeRelevanceCore(fileName, in prepared, fullPath);
    }

    private readonly struct PreparedRankingQuery
    {
        public readonly bool IsEmpty;
        public readonly string NormalizedQuery;
        public readonly string? TrailingLeaf;
        public readonly IReadOnlyList<string> Tokens;

        public PreparedRankingQuery(string rawQuery)
        {
            ReadOnlySpan<char> normalizedSpan = UnwrapSingleOuterQuotes(rawQuery.AsSpan().Trim());
            if (normalizedSpan.IsEmpty || normalizedSpan.IsWhiteSpace())
            {
                IsEmpty = true;
                NormalizedQuery = string.Empty;
                TrailingLeaf = null;
                Tokens = [];
                return;
            }

            this = new PreparedRankingQuery(rawQuery, normalizedSpan);
        }

        public PreparedRankingQuery(string rawQuery, ReadOnlySpan<char> normalizedSpan)
        {
            IsEmpty = false;
            NormalizedQuery = normalizedSpan.ToString();
            if (HasPathSeparator(NormalizedQuery))
            {
                ReadOnlySpan<char> leaf = ExtractTrailingPathLeaf(NormalizedQuery.AsSpan());
                TrailingLeaf = !leaf.IsEmpty && !leaf.IsWhiteSpace() ? leaf.ToString() : null;
            }
            else
            {
                TrailingLeaf = null;
            }

            Tokens = ExtractLiteralTokens(rawQuery);
        }
    }

    private static double ComputeRelevanceCore(
        string fileName,
        in PreparedRankingQuery prepared,
        string? fullPath)
    {
        if (prepared.IsEmpty)
        {
            return 10;
        }

        string normalizedQuery = prepared.NormalizedQuery;
        if (!string.IsNullOrEmpty(fullPath) && PathsEqualNormalized(fullPath, normalizedQuery))
        {
            return 100;
        }

        double directScore = ScoreContiguousFileNameMatch(fileName, normalizedQuery);
        if (directScore > 10)
        {
            return directScore;
        }

        if (prepared.TrailingLeaf is { } leaf)
        {
            double leafScore = ScoreContiguousFileNameMatch(fileName, leaf);
            if (leafScore > 10)
            {
                return Math.Min(leafScore, 95);
            }
        }

        IReadOnlyList<string> tokens = prepared.Tokens;
        if (tokens.Count == 0)
        {
            return 10;
        }

        if (tokens.Count == 1)
        {
            string single = tokens[0];
            if (!string.Equals(single, normalizedQuery, StringComparison.Ordinal))
            {
                double singleScore = ScoreContiguousFileNameMatch(fileName, single);
                if (singleScore > 10)
                {
                    return singleScore;
                }
            }

            return 10;
        }

        int matchedInFileName = 0;
        for (int i = 0; i < tokens.Count; i++)
        {
            string token = tokens[i];
            ReadOnlySpan<char> candidate = HasPathSeparator(token)
                ? ExtractTrailingPathLeaf(token.AsSpan())
                : token.AsSpan();
            if (!candidate.IsEmpty &&
                fileName.AsSpan().Contains(candidate, StringComparison.OrdinalIgnoreCase))
            {
                matchedInFileName++;
            }
        }

        if (matchedInFileName == tokens.Count)
        {
            return fileName.StartsWith(tokens[0], StringComparison.OrdinalIgnoreCase) ? 48 : 45;
        }

        return matchedInFileName > 0 ? 25 : 10;
    }

    private static double ScoreContiguousFileNameMatch(string fileName, string query)
    {
        if (fileName.Equals(query, StringComparison.OrdinalIgnoreCase))
        {
            return 100;
        }

        ReadOnlySpan<char> stem = Path.GetFileNameWithoutExtension(fileName.AsSpan());
        if (stem.Equals(query.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 90;
        }

        if (fileName.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 80;
        }

        if (stem.StartsWith(query.AsSpan(), StringComparison.OrdinalIgnoreCase))
        {
            return 70;
        }

        return fileName.Contains(query, StringComparison.OrdinalIgnoreCase) ? 50 : 10;
    }

    private static ReadOnlySpan<char> UnwrapSingleOuterQuotes(ReadOnlySpan<char> span)
    {
        if (span.Length >= 2 &&
            span[0] == '"' &&
            span[^1] == '"' &&
            span[1..^1].IndexOf('"') < 0)
        {
            return span[1..^1];
        }

        return span;
    }

    private static bool HasPathSeparator(string text) =>
        text.IndexOf('\\') >= 0 || text.IndexOf('/') >= 0;

    private static ReadOnlySpan<char> ExtractTrailingPathLeaf(ReadOnlySpan<char> path)
    {
        int lastSep = path.LastIndexOfAny('\\', '/');
        return lastSep >= 0 ? path[(lastSep + 1)..] : path;
    }

    private static bool PathsEqualNormalized(string left, string right)
    {
        ReadOnlySpan<char> a = TrimTrailingDirectorySeparators(left.AsSpan().Trim());
        ReadOnlySpan<char> b = TrimTrailingDirectorySeparators(right.AsSpan().Trim());
        if (a.Length != b.Length || a.IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < a.Length; i++)
        {
            char ca = a[i] == '/' ? '\\' : a[i];
            char cb = b[i] == '/' ? '\\' : b[i];
            if (ca != cb && char.ToUpperInvariant(ca) != char.ToUpperInvariant(cb))
            {
                return false;
            }
        }

        return true;
    }

    private static ReadOnlySpan<char> TrimTrailingDirectorySeparators(ReadOnlySpan<char> path)
    {
        while (path.Length > 0 && path[^1] is '\\' or '/')
        {
            if (path.Length == 3 && char.IsAsciiLetter(path[0]) && path[1] == ':')
            {
                break;
            }

            path = path[..^1];
        }

        return path;
    }

    private async Task<EverythingConnectionSnapshot> EnsureConnectionForQueryAsync(
        CancellationToken cancellationToken)
    {
        EverythingConnectionSnapshot current = CurrentSnapshot;
        long lastProbe = Interlocked.Read(ref _lastProbeTick);
        long elapsed = Environment.TickCount64 - lastProbe;
        long ttl = current.State == EverythingConnectionState.Connected
            ? ConnectedProbeTtlMilliseconds
            : FailedProbeTtlMilliseconds;
        if (lastProbe != long.MinValue && elapsed >= 0 && elapsed < ttl)
        {
            return current;
        }

        return await RefreshConnectionAsync(
            allowIpcProbe: true,
            cancellationToken).ConfigureAwait(false);
    }

    private static (bool Connected, string? Version, uint Error) ProbeNativeConnection()
    {
        return EverythingNativeMethods.ProbeConnection();
    }

    internal static SearchFileQueryPage QueryNative(
        string providerQuery,
        string rankingQuery,
        int resultOffset,
        int resultCount,
        CancellationToken cancellationToken,
        bool advancedSyntax = false,
        string ipcWindowClass = EverythingNativeMethods.DefaultIpcWindowClass)
    {
        cancellationToken.ThrowIfCancellationRequested();
        uint searchFlags = !advancedSyntax && HasPathSeparator(rankingQuery)
            ? EverythingNativeMethods.SearchFlagMatchPath
            : 0;
        if (!EverythingNativeMethods.TryQuery(
                providerQuery,
                offset: checked((uint)resultOffset),
                maxResults: checked((uint)resultCount),
                cancellationToken,
                out EverythingIpcQueryReply reply,
                out uint errorCode,
                ipcWindowClass: ipcWindowClass,
                searchFlags: searchFlags,
                sortType: EverythingNativeMethods.SortNameAscending))
        {
            throw new EverythingIpcException(errorCode);
        }

        var results = new List<SearchResultItem>(Math.Min(
            Math.Max(reply.Items.Count + (resultOffset == 0 ? SupplementaryTopHitBatchSize * 2 : 0), 1),
            MaximumInitialListCapacity));
        var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var preparedRanking = new PreparedRankingQuery(rankingQuery);
        int totalMatches = ClampToInt(reply.TotalResults);
        uint nativeCount = (uint)reply.Items.Count;

        AppendQueryEntries(reply.Items, in preparedRanking, seenPaths, results, cancellationToken);

        // When a broad query exceeds the first alphabetical page, supplement page 0 with
        // filename-prefix matches and recently modified matches so exact/prefix hits and
        // active user files beyond ASCII alphabetical order are never truncated from page 1.
        if (resultOffset == 0 &&
            reply.TotalResults > nativeCount &&
            rankingQuery.Length >= 2)
        {
            if (!advancedSyntax &&
                BuildSupplementaryPrefixQuery(rankingQuery) is { } prefixQuery)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (EverythingNativeMethods.TryQuery(
                        prefixQuery,
                        offset: 0,
                        maxResults: SupplementaryTopHitBatchSize,
                        cancellationToken,
                        out EverythingIpcQueryReply prefixReply,
                        out _,
                        ipcWindowClass: ipcWindowClass,
                        searchFlags: 0,
                        sortType: EverythingNativeMethods.SortNameAscending))
                {
                    AppendQueryEntries(prefixReply.Items, in preparedRanking, seenPaths, results, cancellationToken);
                }
            }

            if (rankingQuery.Length >= 3 ||
                !Ascii.IsValid(rankingQuery) ||
                reply.TotalResults <= 25_000)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (EverythingNativeMethods.TryQuery(
                        providerQuery,
                        offset: 0,
                        maxResults: SupplementaryTopHitBatchSize,
                        cancellationToken,
                        out EverythingIpcQueryReply recentReply,
                        out _,
                        ipcWindowClass: ipcWindowClass,
                        searchFlags: searchFlags,
                        sortType: EverythingNativeMethods.SortDateModifiedDescending))
                {
                    AppendQueryEntries(recentReply.Items, in preparedRanking, seenPaths, results, cancellationToken);
                }
            }
        }

        int effectiveTotalMatches = Math.Max(totalMatches, results.Count);
        int nextOffset = nativeCount == 0 || (resultOffset == 0 && results.Count >= effectiveTotalMatches)
            ? effectiveTotalMatches
            : (int)Math.Min(effectiveTotalMatches, (long)resultOffset + nativeCount);
        return new SearchFileQueryPage(results, effectiveTotalMatches, nextOffset);
    }

    private static void AppendQueryEntries(
        IReadOnlyList<EverythingIpcResultEntry> entries,
        in PreparedRankingQuery preparedRanking,
        HashSet<string> seenPaths,
        List<SearchResultItem> results,
        CancellationToken cancellationToken)
    {
        foreach (EverythingIpcResultEntry entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FileName.Length == 0)
            {
                continue;
            }

            string fullPath = BuildFullPath(entry.DirectoryPath, entry.FileName);
            if (!seenPaths.Add(fullPath))
            {
                continue;
            }

            DateTimeOffset? modified = null;
            if (entry.DateModifiedFileTime is > 0 and var fileTime)
            {
                try
                {
                    modified = DateTimeOffset.FromFileTime(fileTime);
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Keep the result even if an unusual filesystem timestamp is invalid.
                }
            }

            results.Add(new SearchResultItem
            {
                Kind = entry.IsFolder ? SearchResultKind.Folder : SearchResultKind.File,
                Title = entry.FileName,
                Subtitle = entry.DirectoryPath,
                DetailPath = fullPath,
                FileSize = entry.IsFolder ? null : entry.FileSize,
                ModifiedAt = modified,
                RelevanceScore = entry.FileName.Length == 0
                    ? 0
                    : ComputeRelevanceCore(entry.FileName, in preparedRanking, fullPath)
            });
        }
    }

    private EverythingConnectionSnapshot ClassifyIpcFailure(
        EverythingInstallationSnapshot installation,
        uint error)
    {
        bool permissionMismatch = error == EverythingNativeMethods.ErrorIpc &&
                                  !installation.IsCurrentProcessElevated &&
                                  !installation.HasUnelevatedProcess &&
                                  (installation.HasElevatedProcess ||
                                   installation.ConfiguredToRunAsAdministrator);
        return CreateSnapshot(
            permissionMismatch
                ? EverythingConnectionState.PermissionMismatch
                : EverythingConnectionState.IpcUnavailable,
            installation,
            permissionMismatch ? "integrity-level-mismatch" : $"ipc-error-{error}");
    }

    private static EverythingConnectionSnapshot CreateSnapshot(
        EverythingConnectionState state,
        EverythingInstallationSnapshot installation,
        string? diagnosticCode) => new(
        state,
        installation.ExecutablePath,
        installation.Version,
        installation.IsRunning,
        installation.UsesManualPath,
        diagnosticCode);

    private EverythingConnectionSnapshot PublishSnapshot(
        EverythingConnectionSnapshot snapshot)
    {
        EverythingConnectionSnapshot previous;
        lock (_snapshotLock)
        {
            if (_isDisposed) return _snapshot;
            previous = _snapshot;
            _snapshot = snapshot;
        }

        if (previous != snapshot)
        {
            ConnectionChanged?.Invoke(snapshot);
        }

        return snapshot;
    }

    private static string BuildFullPath(string directory, string fileName)
    {
        if (directory.Length == 0)
        {
            return fileName.Length == 2 && fileName[1] == ':'
                ? fileName + Path.DirectorySeparatorChar
                : fileName;
        }

        return directory.EndsWith(Path.DirectorySeparatorChar) ||
               directory.EndsWith(Path.AltDirectorySeparatorChar)
            ? directory + fileName
            : Path.Combine(directory, fileName);
    }

    private static int ClampToInt(uint value) =>
        value > int.MaxValue ? int.MaxValue : (int)value;

    public void Dispose()
    {
        _isDisposed = true;
    }

    private sealed class EverythingIpcException(uint errorCode) : Exception
    {
        public uint ErrorCode { get; } = errorCode;
    }
}
