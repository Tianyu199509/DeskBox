using System.Globalization;
using System.Text;

namespace DeskBox.Services.Lyrics;

/// <summary>
/// Resolves user-supplied local lyric files (<c>.qrc</c>, <c>.yrc</c>, <c>.krc</c>, <c>.lrcx</c>, <c>.lrc</c>,
/// <c>.ttml</c>, <c>.srt</c>, <c>.vtt</c>, NetEase <c>.json</c>, <c>.txt</c>) with UTF-8/GB18030 detection,
/// player cache filename matching, and automatic companion translation merging.
/// </summary>
public static class LocalFileLyricsProvider
{
    private const long MaxLyricFileBytes = 2 * 1024 * 1024;
    private const int MaxScannedFilesPerSearch = 10_000;
    private const double MinimumLocalAcceptScore = 0.74;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static int s_codePagesRegistered;

    private static readonly string[] SupportedExtensions =
    [
        ".qrc",
        ".yrc",
        ".krc",
        ".lrcx",
        ".lrc",
        ".ttml",
        ".srt",
        ".vtt",
        ".json",
        ".txt"
    ];

    private static readonly string[] KnownCacheSubfolders =
    [
        "QQMusicLyricNew",
        "QQMusicLyric",
        "Lyric",
        "Lyrics"
    ];

    private static readonly string[] AudioDoubleExtensions =
    [
        ".flac",
        ".mp3",
        ".m4a",
        ".wav",
        ".ogg",
        ".opus",
        ".ape",
        ".aac",
        ".wma",
        ".alac",
        ".dsf"
    ];

    private static readonly string[] IgnoredSidecarSuffixes =
    [
        "_qmroma",
        "_romaji",
        ".romaji",
        "_roma",
        ".roma",
        "_qmts",
        "_trans",
        ".trans",
        "_ts",
        ".ts",
        "_tr",
        ".tr",
        "_chs",
        ".chs",
        "_cht",
        ".cht",
        "_zh",
        ".zh"
    ];

    private readonly record struct ParsedLocalStem(
        string RawStem,
        string CleanStem,
        string? Artist,
        string? Title,
        int? DurationSeconds);

    private readonly record struct ScoredLocalFile(
        string FilePath,
        double Score,
        int ExtensionPriority,
        int? DurationSeconds);

    private readonly record struct IndexedLocalLyricFile(
        string FilePath,
        int ExtensionPriority,
        ParsedLocalStem ParsedStem);

    private readonly record struct PreparedLyricsQuery(
        LyricsQuery Query,
        string CleanTitle,
        string CleanArtist,
        string PrimaryArtist,
        string NormArtist,
        string NormPrimaryArtist)
    {
        public static PreparedLyricsQuery Create(LyricsQuery query)
        {
            string cleanTitle = LyricTrackMatcher.CleanTitle(query.Title);
            string cleanArtist = string.IsNullOrWhiteSpace(query.Artist)
                ? string.Empty
                : LyricTrackMatcher.CleanArtist(query.Artist);
            string primaryArtist = string.IsNullOrWhiteSpace(query.Artist)
                ? string.Empty
                : StripTrailingParentheticalAlias(LyricTrackMatcher.ExtractPrimaryArtist(query.Artist));
            string normArtist = string.IsNullOrWhiteSpace(query.Artist)
                ? string.Empty
                : LyricTrackMatcher.NormalizeForComparison(query.Artist);
            string normPrimary = string.IsNullOrWhiteSpace(primaryArtist)
                ? string.Empty
                : LyricTrackMatcher.NormalizeForComparison(primaryArtist);
            return new PreparedLyricsQuery(
                query,
                cleanTitle,
                cleanArtist,
                primaryArtist,
                normArtist,
                normPrimary);
        }
    }

    private sealed class CachedDirectoryIndex
    {
        public required string DirectoryPath { get; init; }
        public required DateTime DirectoryLastWriteUtc { get; init; }
        public required IndexedLocalLyricFile[] Entries { get; init; }
        public DateTimeOffset LastAccessedUtc { get; set; }
    }

    private const int MaxCachedDirectories = 4;
    private static readonly TimeSpan DefaultDirectoryIndexIdleTtl = TimeSpan.FromMinutes(10);
    private static readonly object s_directoryIndexGate = new();
    private static readonly Dictionary<string, CachedDirectoryIndex> s_directoryIndexCache =
        new(StringComparer.OrdinalIgnoreCase);

    internal static int CachedDirectoryIndexCount
    {
        get
        {
            lock (s_directoryIndexGate)
            {
                return s_directoryIndexCache.Count;
            }
        }
    }

    public static void ClearDirectoryIndexCache()
    {
        lock (s_directoryIndexGate)
        {
            s_directoryIndexCache.Clear();
        }
    }

    internal static void TrimIdleDirectoryIndex(TimeSpan? maxIdle = null)
    {
        TimeSpan ttl = maxIdle ?? DefaultDirectoryIndexIdleTtl;
        DateTimeOffset now = DateTimeOffset.UtcNow;
        lock (s_directoryIndexGate)
        {
            if (s_directoryIndexCache.Count == 0)
            {
                return;
            }

            List<string>? expiredKeys = null;
            foreach (KeyValuePair<string, CachedDirectoryIndex> pair in s_directoryIndexCache)
            {
                if (now - pair.Value.LastAccessedUtc >= ttl)
                {
                    expiredKeys ??= new List<string>(s_directoryIndexCache.Count);
                    expiredKeys.Add(pair.Key);
                }
            }

            if (expiredKeys is not null)
            {
                for (int i = 0; i < expiredKeys.Count; i++)
                {
                    s_directoryIndexCache.Remove(expiredKeys[i]);
                }
            }
        }
    }

    private static IndexedLocalLyricFile[] GetOrBuildDirectoryIndex(
        string dir,
        ref int scannedFiles,
        CancellationToken cancellationToken)
    {
        DateTime lastWriteUtc;
        try
        {
            lastWriteUtc = Directory.GetLastWriteTimeUtc(dir);
        }
        catch
        {
            return [];
        }

        lock (s_directoryIndexGate)
        {
            if (s_directoryIndexCache.TryGetValue(dir, out CachedDirectoryIndex? cached) &&
                cached.DirectoryLastWriteUtc == lastWriteUtc)
            {
                cached.LastAccessedUtc = DateTimeOffset.UtcNow;
                scannedFiles += cached.Entries.Length;
                return cached.Entries;
            }
        }

        IEnumerable<string> rawPaths;
        try
        {
            rawPaths = Directory.EnumerateFiles(dir);
        }
        catch
        {
            return [];
        }

        var indexed = new List<IndexedLocalLyricFile>(256);
        foreach (string filePath in rawPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++scannedFiles > MaxScannedFilesPerSearch)
            {
                break;
            }

            string ext = Path.GetExtension(filePath);
            int extPriority = GetExtensionPriority(ext);
            if (extPriority <= 0)
            {
                continue;
            }

            string rawStem = Path.GetFileNameWithoutExtension(filePath);
            if (string.IsNullOrWhiteSpace(rawStem) || IsSidecarTranslationOrRomajiStem(rawStem))
            {
                continue;
            }

            ParsedLocalStem parsedStem = ParseFilenameStem(rawStem);
            indexed.Add(new IndexedLocalLyricFile(filePath, extPriority, parsedStem));
        }

        IndexedLocalLyricFile[] entries = indexed.ToArray();
        lock (s_directoryIndexGate)
        {
            if (!s_directoryIndexCache.ContainsKey(dir) &&
                s_directoryIndexCache.Count >= MaxCachedDirectories)
            {
                string? oldestKey = null;
                DateTimeOffset oldestAccess = DateTimeOffset.MaxValue;
                foreach (KeyValuePair<string, CachedDirectoryIndex> pair in s_directoryIndexCache)
                {
                    if (pair.Value.LastAccessedUtc < oldestAccess)
                    {
                        oldestAccess = pair.Value.LastAccessedUtc;
                        oldestKey = pair.Key;
                    }
                }

                if (oldestKey is not null)
                {
                    s_directoryIndexCache.Remove(oldestKey);
                }
            }

            s_directoryIndexCache[dir] = new CachedDirectoryIndex
            {
                DirectoryPath = dir,
                DirectoryLastWriteUtc = lastWriteUtc,
                Entries = entries,
                LastAccessedUtc = DateTimeOffset.UtcNow
            };
        }

        return entries;
    }

    public static async Task<LrcDocument?> TryResolveLocalLyricsAsync(
        LyricsQuery query,
        string? localFolder,
        int userOffsetMs = 0,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsValid || string.IsNullOrWhiteSpace(localFolder))
        {
            return null;
        }

        List<string>? searchDirs = ResolveCandidateDirectories(localFolder, query);
        if (searchDirs is null || searchDirs.Count == 0)
        {
            return null;
        }

        // 1. Fast O(1) exact path probes before enumerating any directory entries.
        List<string> exactBaseNames = BuildExactCandidateBaseNames(query);
        foreach (string dir in searchDirs)
        {
            foreach (string baseName in exactBaseNames)
            {
                foreach (string ext in SupportedExtensions)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    string candidatePath = Path.Combine(dir, baseName + ext);
                    if (!File.Exists(candidatePath))
                    {
                        continue;
                    }

                    LrcDocument? resolved = await TryDecodeDocumentWithCompanionAsync(
                            candidatePath,
                            query.Duration,
                            filenameDurationSeconds: null,
                            userOffsetMs,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (resolved is not null && (resolved.HasSyncedLines || resolved.IsInstrumental))
                    {
                        return resolved;
                    }
                }
            }
        }

        // 2. Bounded smart filename scan (handles QQMusic *_qm.qrc, Kugou -{md5}.krc, 01 - Artist - Title.flac.lrc, etc.).
        var matches = new List<ScoredLocalFile>(8);
        int scannedFiles = 0;
        PreparedLyricsQuery preparedQuery = PreparedLyricsQuery.Create(query);

        foreach (string dir in searchDirs)
        {
            if (scannedFiles >= MaxScannedFilesPerSearch)
            {
                break;
            }

            IndexedLocalLyricFile[] entries = GetOrBuildDirectoryIndex(
                dir,
                ref scannedFiles,
                cancellationToken);
            for (int i = 0; i < entries.Length; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IndexedLocalLyricFile entry = entries[i];
                double score = ScoreParsedStem(entry.ParsedStem, in preparedQuery);
                if (score >= MinimumLocalAcceptScore)
                {
                    matches.Add(new ScoredLocalFile(
                        entry.FilePath,
                        score,
                        entry.ExtensionPriority,
                        entry.ParsedStem.DurationSeconds));
                }
            }
        }

        if (matches.Count == 0)
        {
            return null;
        }

        matches.Sort(static (a, b) =>
        {
            int scoreCmp = b.Score.CompareTo(a.Score);
            if (Math.Abs(a.Score - b.Score) > 0.005 && scoreCmp != 0)
            {
                return scoreCmp;
            }

            int prioCmp = b.ExtensionPriority.CompareTo(a.ExtensionPriority);
            return prioCmp != 0 ? prioCmp : scoreCmp;
        });

        int maxAttempts = Math.Min(matches.Count, 4);
        for (int i = 0; i < maxAttempts; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ScoredLocalFile candidate = matches[i];
            LrcDocument? resolved = await TryDecodeDocumentWithCompanionAsync(
                    candidate.FilePath,
                    query.Duration,
                    candidate.DurationSeconds,
                    userOffsetMs,
                    cancellationToken)
                .ConfigureAwait(false);
            if (resolved is not null && (resolved.HasSyncedLines || resolved.IsInstrumental))
            {
                return resolved;
            }
        }

        return null;
    }

    public static async Task<string?> TryReadLocalLrcAsync(
        LyricsQuery query,
        string? localFolder,
        CancellationToken cancellationToken = default)
    {
        if (!query.IsValid || string.IsNullOrWhiteSpace(localFolder))
        {
            return null;
        }

        string folder;
        try
        {
            folder = Path.GetFullPath(localFolder.Trim());
            if (!Directory.Exists(folder))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        List<string> candidates = BuildExactCandidateBaseNames(query);
        foreach (string fileName in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fullPath = Path.Combine(folder, fileName + ".lrc");
            if (!File.Exists(fullPath))
            {
                continue;
            }

            string? text = await ReadTextWithEncodingFallbackAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    internal static string DecodeLrcBytes(byte[] bytes)
    {
        if (bytes.Length == 0)
        {
            return string.Empty;
        }

        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            EnsureCodePagesRegistered();
            try
            {
                return Encoding.GetEncoding(54936).GetString(bytes); // GB18030 (superset of GBK/GB2312)
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }

    private static async Task<LrcDocument?> TryDecodeDocumentWithCompanionAsync(
        string primaryPath,
        TimeSpan queryDuration,
        int? filenameDurationSeconds,
        int userOffsetMs,
        CancellationToken cancellationToken)
    {
        byte[]? rawBytes = await ReadBoundedBytesAsync(primaryPath, cancellationToken).ConfigureAwait(false);
        if (rawBytes is null || rawBytes.Length == 0)
        {
            return null;
        }

        string ext = Path.GetExtension(primaryPath);
        DecodedLocalLyricsPayload payload = LocalLyricsFormatDecoder.DecodeBytes(rawBytes, ext);
        if (!payload.HasContent)
        {
            return null;
        }

        TimeSpan? knownDuration = queryDuration.TotalSeconds > 0
            ? queryDuration
            : (filenameDurationSeconds is > 0 ? TimeSpan.FromSeconds(filenameDurationSeconds.Value) : null);

        if (payload.IsInstrumental && string.IsNullOrWhiteSpace(payload.PrimaryText))
        {
            return new LrcDocument([], isInstrumental: true, knownDuration, embeddedOffsetMs: 0, userOffsetMs);
        }

        LrcDocument doc = LrcParser.Parse(payload.PrimaryText, knownDuration, userOffsetMs);
        if (!doc.HasSyncedLines && !doc.IsInstrumental)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(payload.TranslationText))
        {
            doc = LrcParser.MergeTranslation(doc, payload.TranslationText);
        }
        else if (doc.HasSyncedLines)
        {
            string? companionTranslation = await TryReadCompanionTranslationTextAsync(primaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(companionTranslation))
            {
                doc = LrcParser.MergeTranslation(doc, companionTranslation);
            }
        }

        if (filenameDurationSeconds is > 0)
        {
            doc = doc.WithTrackDurationIfMissing(TimeSpan.FromSeconds(filenameDurationSeconds.Value));
        }

        return doc;
    }

    private static async Task<string?> TryReadCompanionTranslationTextAsync(
        string primaryPath,
        CancellationToken cancellationToken)
    {
        string? dir = Path.GetDirectoryName(primaryPath);
        if (string.IsNullOrEmpty(dir))
        {
            return null;
        }

        string stem = Path.GetFileNameWithoutExtension(primaryPath);
        string ext = Path.GetExtension(primaryPath);
        var companionPaths = new List<string>(10);

        if (stem.EndsWith("_qm", StringComparison.OrdinalIgnoreCase))
        {
            string qmPrefix = stem[..^3];
            AddUniquePath(companionPaths, Path.Combine(dir, qmPrefix + "_qmts.qrc"));
            AddUniquePath(companionPaths, Path.Combine(dir, qmPrefix + "_qmts.lrc"));
        }

        AddUniquePath(companionPaths, Path.Combine(dir, stem + "_qmts.qrc"));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + "_ts" + ext));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + "_ts.lrc"));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + ".trans" + ext));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + ".trans.lrc"));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + "_trans" + ext));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + "_trans.lrc"));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + ".zh" + ext));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + ".zh.lrc"));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + ".chs" + ext));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + ".chs.lrc"));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + "_tr" + ext));
        AddUniquePath(companionPaths, Path.Combine(dir, stem + "_tr.lrc"));

        foreach (string companionPath in companionPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(companionPath))
            {
                continue;
            }

            byte[]? bytes = await ReadBoundedBytesAsync(companionPath, cancellationToken).ConfigureAwait(false);
            if (bytes is null || bytes.Length == 0)
            {
                continue;
            }

            DecodedLocalLyricsPayload decoded = LocalLyricsFormatDecoder.DecodeBytes(bytes, Path.GetExtension(companionPath));
            if (!string.IsNullOrWhiteSpace(decoded.PrimaryText))
            {
                return decoded.PrimaryText;
            }
        }

        return null;
    }

    private static List<string>? ResolveCandidateDirectories(string localFolder, LyricsQuery query)
    {
        string root;
        try
        {
            root = Path.GetFullPath(localFolder.Trim());
            if (!Directory.Exists(root))
            {
                return null;
            }
        }
        catch
        {
            return null;
        }

        var dirs = new List<string>(6) { root };

        foreach (string sub in KnownCacheSubfolders)
        {
            TryAddExistingDirectory(dirs, Path.Combine(root, sub));
        }

        string primaryArtist = SanitizeFileNameSegment(LyricTrackMatcher.ExtractPrimaryArtist(query.Artist));
        if (!string.IsNullOrWhiteSpace(primaryArtist))
        {
            string artistDir = Path.Combine(root, primaryArtist);
            TryAddExistingDirectory(dirs, artistDir);

            string cleanAlbum = SanitizeFileNameSegment(query.Album);
            if (!string.IsNullOrWhiteSpace(cleanAlbum))
            {
                TryAddExistingDirectory(dirs, Path.Combine(artistDir, cleanAlbum));
            }
        }

        return dirs;
    }

    private static void TryAddExistingDirectory(List<string> dirs, string candidate)
    {
        try
        {
            if (Directory.Exists(candidate))
            {
                AddUniquePath(dirs, candidate);
            }
        }
        catch
        {
        }
    }

    private static List<string> BuildExactCandidateBaseNames(LyricsQuery query)
    {
        string rawTitle = SanitizeFileNameSegment(query.Title);
        string cleanTitle = SanitizeFileNameSegment(LyricTrackMatcher.CleanTitle(query.Title));
        string rawArtist = SanitizeFileNameSegment(query.Artist);
        string primaryArtist = SanitizeFileNameSegment(LyricTrackMatcher.ExtractPrimaryArtist(query.Artist));
        string rawAlbum = SanitizeFileNameSegment(query.Album);

        var candidates = new List<string>(10);

        if (query.Duration.TotalSeconds >= 5.0 && !string.IsNullOrWhiteSpace(rawArtist) && !string.IsNullOrWhiteSpace(rawTitle))
        {
            int roundedSeconds = (int)Math.Round(query.Duration.TotalSeconds);
            if (!string.IsNullOrWhiteSpace(rawAlbum))
            {
                AddUnique(candidates, $"{rawArtist} - {rawTitle} - {roundedSeconds.ToString(CultureInfo.InvariantCulture)} - {rawAlbum}_qm");
                if (roundedSeconds > 5)
                {
                    AddUnique(candidates, $"{rawArtist} - {rawTitle} - {(roundedSeconds - 1).ToString(CultureInfo.InvariantCulture)} - {rawAlbum}_qm");
                }

                AddUnique(candidates, $"{rawArtist} - {rawTitle} - {(roundedSeconds + 1).ToString(CultureInfo.InvariantCulture)} - {rawAlbum}_qm");
            }
        }

        AddCandidateNames(candidates, rawArtist, rawTitle);
        AddCandidateNames(candidates, primaryArtist, cleanTitle);
        AddSingleName(candidates, rawTitle);
        AddSingleName(candidates, cleanTitle);
        return candidates;
    }

    private static ParsedLocalStem ParseFilenameStem(string rawStem)
    {
        string working = StripTrailingAudioExtension(rawStem.Trim());
        bool hadQmSuffix = false;

        if (working.EndsWith("_qm", StringComparison.OrdinalIgnoreCase) && working.Length > 3)
        {
            hadQmSuffix = true;
            working = working[..^3].TrimEnd();
        }

        working = TryStripKugouHashSuffix(working);

        // Check QQMusic "{Artist} - {Title} - {DurationSeconds} - {Album}" naming convention.
        if (working.Contains(" - ", StringComparison.Ordinal))
        {
            string[] parts = working.Split(" - ", StringSplitOptions.None);
            if (parts.Length >= 4 || (hadQmSuffix && parts.Length == 3))
            {
                int maxDurationIdx = parts.Length >= 4 ? parts.Length - 2 : 2;
                for (int k = 2; k <= maxDurationIdx; k++)
                {
                    if (int.TryParse(parts[k].AsSpan().Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int durSec) &&
                        durSec is >= 5 and <= 36_000)
                    {
                        string qmArtist = parts[0].Trim();
                        string qmTitle = string.Join(" - ", parts[1..k]).Trim();
                        if (!string.IsNullOrWhiteSpace(qmTitle))
                        {
                            return new ParsedLocalStem(rawStem, working, qmArtist, qmTitle, durSec);
                        }
                    }
                }
            }
        }

        string cleanStem = StripLeadingTrackNumber(working);
        return new ParsedLocalStem(rawStem, cleanStem, null, null, null);
    }

    private static double ScoreParsedStem(ParsedLocalStem stem, in PreparedLyricsQuery prepared)
    {
        if (!string.IsNullOrWhiteSpace(stem.Title))
        {
            return ScoreArtistTitlePair(stem.Artist, stem.Title, stem.DurationSeconds, in prepared);
        }

        double best = ScoreTitleOnlyStem(stem.RawStem, in prepared);
        if (!string.Equals(stem.CleanStem, stem.RawStem, StringComparison.Ordinal))
        {
            best = Math.Max(best, ScoreTitleOnlyStem(stem.CleanStem, in prepared));
        }

        string target = stem.CleanStem;
        int firstSep = target.IndexOf(" - ", StringComparison.Ordinal);
        if (firstSep > 0 && firstSep < target.Length - 3)
        {
            // Interpretation A: "{Artist} - {Title}"
            string artistA = target[..firstSep].Trim();
            string titleA = target[(firstSep + 3)..].Trim();
            best = Math.Max(best, ScoreArtistTitlePair(artistA, titleA, null, in prepared));

            // Interpretation B: "{Title} - {Artist}"
            int lastSep = target.LastIndexOf(" - ", StringComparison.Ordinal);
            string titleB = target[..lastSep].Trim();
            string artistB = target[(lastSep + 3)..].Trim();
            best = Math.Max(best, ScoreArtistTitlePair(artistB, titleB, null, in prepared));
        }

        return best;
    }

    private static double ScoreTitleOnlyStem(string candidateTitle, in PreparedLyricsQuery prepared)
    {
        double rawSim = LyricTrackMatcher.ComputeTextSimilarity(prepared.Query.Title, candidateTitle);
        double cleanSim = LyricTrackMatcher.ComputeTextSimilarity(
            prepared.CleanTitle,
            LyricTrackMatcher.CleanTitle(candidateTitle));
        double titleScore = Math.Max(rawSim, cleanSim * 0.98);
        if (titleScore < 0.92)
        {
            return 0.0;
        }

        return (0.65 * titleScore) + (0.35 * 0.72);
    }

    private static double ScoreArtistTitlePair(
        string? candidateArtist,
        string candidateTitle,
        int? candidateDurationSec,
        in PreparedLyricsQuery prepared)
    {
        LyricsQuery query = prepared.Query;
        double rawTitleSim = LyricTrackMatcher.ComputeTextSimilarity(query.Title, candidateTitle);
        double cleanTitleSim = LyricTrackMatcher.ComputeTextSimilarity(
            prepared.CleanTitle,
            LyricTrackMatcher.CleanTitle(candidateTitle));
        double titleScore = Math.Max(rawTitleSim, cleanTitleSim * 0.98);
        if (titleScore < 0.72)
        {
            return 0.0;
        }

        double artistScore;
        if (string.IsNullOrWhiteSpace(candidateArtist))
        {
            if (titleScore < 0.92)
            {
                return 0.0;
            }

            artistScore = 0.72;
        }
        else if (string.IsNullOrWhiteSpace(query.Artist))
        {
            artistScore = 0.75;
        }
        else
        {
            string candCleanArtist = LyricTrackMatcher.CleanArtist(candidateArtist);
            string candPrimary = StripTrailingParentheticalAlias(LyricTrackMatcher.ExtractPrimaryArtist(candidateArtist));

            double fullSim = LyricTrackMatcher.ComputeTextSimilarity(prepared.CleanArtist, candCleanArtist);
            double primarySim = LyricTrackMatcher.ComputeTextSimilarity(prepared.PrimaryArtist, candPrimary);

            string normCand = LyricTrackMatcher.NormalizeForComparison(candidateArtist);
            string normCandPrimary = LyricTrackMatcher.NormalizeForComparison(candPrimary);

            bool containsPrimary =
                (prepared.NormPrimaryArtist.Length >= 2 && normCand.Contains(prepared.NormPrimaryArtist, StringComparison.Ordinal)) ||
                (normCandPrimary.Length >= 2 && prepared.NormArtist.Contains(normCandPrimary, StringComparison.Ordinal));

            artistScore = Math.Max(fullSim, Math.Max(primarySim * 0.97, containsPrimary ? 0.88 : 0.0));
            if (artistScore < 0.45)
            {
                return 0.0;
            }
        }

        double durationAdjustment = 0.0;
        if (candidateDurationSec is > 5 && query.Duration.TotalSeconds > 5.0)
        {
            double delta = Math.Abs(query.Duration.TotalSeconds - candidateDurationSec.Value);
            if (delta <= 3.0)
            {
                durationAdjustment = 0.06;
            }
            else if (delta <= 8.0)
            {
                durationAdjustment = 0.03;
            }
            else if (delta > 25.0)
            {
                durationAdjustment = -0.15;
            }
        }

        return Math.Clamp((0.65 * titleScore) + (0.35 * artistScore) + durationAdjustment, 0.0, 1.0);
    }

    private static string StripTrailingParentheticalAlias(string artist)
    {
        if (string.IsNullOrWhiteSpace(artist))
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = artist.AsSpan().Trim();
        int openParen = span.IndexOfAny('(', '（');
        if (openParen > 0)
        {
            ReadOnlySpan<char> prefix = span[..openParen].Trim();
            if (!prefix.IsEmpty)
            {
                return prefix.ToString();
            }
        }

        return span.ToString();
    }

    private static string StripTrailingAudioExtension(string stem)
    {
        for (int i = 0; i < AudioDoubleExtensions.Length; i++)
        {
            string audioExt = AudioDoubleExtensions[i];
            if (stem.Length > audioExt.Length &&
                stem.EndsWith(audioExt, StringComparison.OrdinalIgnoreCase))
            {
                return stem[..^audioExt.Length].TrimEnd();
            }
        }

        return stem;
    }

    private static string TryStripKugouHashSuffix(string stem)
    {
        // Kugou naming pattern: "{Artist} - {Title}-{32hexHash}-{id}-{ver}" or "{Artist} - {Title}-{32hexHash}"
        for (int i = stem.Length - 33; i >= 1; i--)
        {
            if (stem[i] != '-')
            {
                continue;
            }

            ReadOnlySpan<char> maybeHash = stem.AsSpan(i + 1, 32);
            if (!IsAllAsciiHex(maybeHash))
            {
                continue;
            }

            ReadOnlySpan<char> tail = stem.AsSpan(i + 33);
            if (tail.IsEmpty || (tail[0] == '-' && IsKugouNumericTail(tail[1..])))
            {
                string prefix = stem[..i].TrimEnd();
                if (!string.IsNullOrWhiteSpace(prefix))
                {
                    return prefix;
                }
            }
        }

        return stem;
    }

    private static bool IsAllAsciiHex(ReadOnlySpan<char> span)
    {
        for (int i = 0; i < span.Length; i++)
        {
            if (!char.IsAsciiHexDigit(span[i]))
            {
                return false;
            }
        }

        return span.Length > 0;
    }

    private static bool IsKugouNumericTail(ReadOnlySpan<char> tail)
    {
        if (tail.IsEmpty)
        {
            return false;
        }

        for (int i = 0; i < tail.Length; i++)
        {
            char c = tail[i];
            if (!char.IsAsciiHexDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    private static string StripLeadingTrackNumber(string stem)
    {
        ReadOnlySpan<char> span = stem.AsSpan().TrimStart();
        int idx = 0;

        // Optional disc prefix "1-" before 1-3 track digits
        if (span.Length >= 5 && char.IsAsciiDigit(span[0]) && span[1] == '-' && char.IsAsciiDigit(span[2]))
        {
            idx = 2;
        }

        int digits = 0;
        while (idx < span.Length && char.IsAsciiDigit(span[idx]) && digits < 3)
        {
            idx++;
            digits++;
        }

        if (digits == 0 || idx >= span.Length)
        {
            return stem;
        }

        ReadOnlySpan<char> afterDigits = span[idx..];
        if (afterDigits.StartsWith(". ", StringComparison.Ordinal))
        {
            afterDigits = afterDigits[2..];
        }
        else if (afterDigits.StartsWith(" - ", StringComparison.Ordinal))
        {
            afterDigits = afterDigits[3..];
        }
        else if (afterDigits.StartsWith(".", StringComparison.Ordinal) || afterDigits.StartsWith("-", StringComparison.Ordinal))
        {
            afterDigits = afterDigits[1..].TrimStart();
        }
        else
        {
            return stem;
        }

        afterDigits = afterDigits.TrimStart();
        return afterDigits.IsEmpty ? stem : afterDigits.ToString();
    }

    private static bool IsSidecarTranslationOrRomajiStem(string stem)
    {
        for (int i = 0; i < IgnoredSidecarSuffixes.Length; i++)
        {
            string suffix = IgnoredSidecarSuffixes[i];
            if (stem.Length > suffix.Length && stem.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int GetExtensionPriority(string? ext)
    {
        if (string.IsNullOrEmpty(ext))
        {
            return 0;
        }

        if (ext.Equals(".qrc", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".yrc", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".krc", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".lrcx", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".lrc", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".ttml", StringComparison.OrdinalIgnoreCase))
        {
            return 3;
        }

        if (ext.Equals(".srt", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".vtt", StringComparison.OrdinalIgnoreCase) ||
            ext.Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            return 2;
        }

        if (ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return 0;
    }

    private static async Task<byte[]?> ReadBoundedBytesAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(filePath);
            if (!info.Exists || info.Length <= 0 || info.Length > MaxLyricFileBytes)
            {
                return null;
            }

            return await File.ReadAllBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> ReadTextWithEncodingFallbackAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        byte[]? bytes = await ReadBoundedBytesAsync(filePath, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : DecodeLrcBytes(bytes);
    }

    private static void EnsureCodePagesRegistered()
    {
        if (Interlocked.Exchange(ref s_codePagesRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    private static void AddCandidateNames(List<string> list, string artist, string title)
    {
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return;
        }

        AddUnique(list, $"{artist} - {title}");
        AddUnique(list, $"{title} - {artist}");
    }

    private static void AddSingleName(List<string> list, string title)
    {
        if (!string.IsNullOrWhiteSpace(title))
        {
            AddUnique(list, title);
        }
    }

    private static void AddUnique(List<string> list, string value)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        list.Add(value);
    }

    private static void AddUniquePath(List<string> list, string path)
    {
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], path, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        list.Add(path);
    }

    private static string SanitizeFileNameSegment(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return string.Empty;
        }

        char[] invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(input.Length);
        foreach (char c in input.Trim())
        {
            if (Array.IndexOf(invalid, c) < 0)
            {
                sb.Append(c);
            }
        }

        return sb.ToString().Trim();
    }
}
