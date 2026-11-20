using System.IO;
using System.Text;
using System.Text.Json;
using DeskBox.Platform;

namespace DeskBox.Services;

public sealed class CustomWidgetStudioPreferencesStore : IDisposable
{
    public const string ChannelAuto = "";
    public const string ChannelDirect = "direct";
    public const string ChannelAgentMcp = "agent-mcp";

    public const string EditorAuto = "auto";
    public const string EditorCursor = "cursor";
    public const string EditorAntigravity = "antigravity";
    public const string EditorVsCode = "vscode";
    public const string EditorTrae = "trae";
    public const string EditorWindsurf = "windsurf";

    public const string StageThemeAuto = "auto";
    public const string StageThemeLight = "light";
    public const string StageThemeDark = "dark";

    public static CustomWidgetStudioPreferencesStore Shared { get; } = new();

    private readonly object _gate = new();
    private readonly string _filePath;
    private readonly string _watchedFileName;
    private FileSystemWatcher? _watcher;
    private CancellationTokenSource? _watcherDebounceCts;
    private bool _loaded;
    private bool _disposed;
    private long _lastObservedWriteTicks = -1;
    private long _lastSelfWriteTickMs;

    private bool _hasSeenPickElementTip;
    private bool _hasSeenAgentMcpGuide;
    private bool _hasSeenDirectStepTour;
    private bool _hasSeenAgentStepTour;
    private string _preferredStudioChannel = string.Empty;
    private bool _locateOnDesktopAfterCommit = true;
    private string _preferredAiProviderId = string.Empty;
    private string _preferredCodeEditor = EditorAuto;
    private string _stageWidgetTheme = StageThemeAuto;
    private bool _sendOnEnter;

    public event EventHandler? PreferencesChanged;

    public CustomWidgetStudioPreferencesStore(string? customFilePath = null)
    {
        _filePath = string.IsNullOrWhiteSpace(customFilePath)
            ? Path.Combine(DeskBoxDataPathService.Current.DataDirectory, CustomWidgetDataStore.SubdirectoryName, "studio-prefs.json")
            : customFilePath;
        _watchedFileName = Path.GetFileName(_filePath);
        TryStartWatcher();
    }

    internal string FilePath => _filePath;

    public bool HasSeenPickElementTip
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _hasSeenPickElementTip;
            }
        }
    }

    public bool HasSeenAgentMcpGuide
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _hasSeenAgentMcpGuide;
            }
        }
    }

    public string PreferredStudioChannel
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _preferredStudioChannel;
            }
        }
    }

    public bool LocateOnDesktopAfterCommit
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _locateOnDesktopAfterCommit;
            }
        }
    }

    public void MarkPickElementTipSeen()
    {
        bool changed = false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!_hasSeenPickElementTip)
            {
                _hasSeenPickElementTip = true;
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaisePreferencesChanged();
        }
    }

    public void MarkAgentMcpGuideSeen()
    {
        bool changed = false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!_hasSeenAgentMcpGuide)
            {
                _hasSeenAgentMcpGuide = true;
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaisePreferencesChanged();
        }
    }

    public bool HasSeenChannelStepTour(string? channel)
    {
        string normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        lock (_gate)
        {
            EnsureLoadedLocked();
            return normalized switch
            {
                ChannelDirect => _hasSeenDirectStepTour,
                ChannelAgentMcp => _hasSeenAgentStepTour,
                _ => false
            };
        }
    }

    public void MarkChannelStepTourSeen(string? channel)
    {
        string normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        bool changed = false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            switch (normalized)
            {
                case ChannelDirect:
                    if (_hasSeenDirectStepTour) return;
                    _hasSeenDirectStepTour = true;
                    break;
                case ChannelAgentMcp:
                    if (_hasSeenAgentStepTour) return;
                    _hasSeenAgentStepTour = true;
                    break;
                default:
                    return;
            }

            SaveLocked();
            changed = true;
        }

        if (changed)
        {
            RaisePreferencesChanged();
        }
    }

    public void SetPreferredStudioChannel(string? channel)
    {
        string normalized = (channel ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not (ChannelAuto or ChannelDirect or ChannelAgentMcp))
        {
            normalized = ChannelAuto;
        }

        bool changed = false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!string.Equals(_preferredStudioChannel, normalized, StringComparison.Ordinal))
            {
                _preferredStudioChannel = normalized;
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaisePreferencesChanged();
        }
    }

    public void SetLocateOnDesktopAfterCommit(bool locate)
    {
        bool changed = false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_locateOnDesktopAfterCommit != locate)
            {
                _locateOnDesktopAfterCommit = locate;
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaisePreferencesChanged();
        }
    }

    /// <summary>
    /// Provider the user picked in Settings for one-click generation. Empty
    /// means "first provider that has a key".
    /// </summary>
    public string PreferredAiProviderId
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _preferredAiProviderId;
            }
        }
    }

    public void SetPreferredAiProviderId(string? providerId)
    {
        string normalized = providerId?.Trim() ?? string.Empty;
        bool changed = false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!string.Equals(_preferredAiProviderId, normalized, StringComparison.OrdinalIgnoreCase))
            {
                _preferredAiProviderId = normalized;
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaisePreferencesChanged();
        }
    }

    public string PreferredCodeEditor
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _preferredCodeEditor;
            }
        }
    }

    public void SetPreferredCodeEditor(string? editorId)
    {
        string normalized = (editorId ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not (EditorAuto or EditorCursor or EditorAntigravity or EditorVsCode or EditorTrae or EditorWindsurf))
        {
            normalized = EditorAuto;
        }

        bool changed = false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!string.Equals(_preferredCodeEditor, normalized, StringComparison.Ordinal))
            {
                _preferredCodeEditor = normalized;
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaisePreferencesChanged();
        }
    }

    public string StageWidgetTheme
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _stageWidgetTheme;
            }
        }
    }

    public void SetStageWidgetTheme(string? themeMode)
    {
        string normalized = (themeMode ?? string.Empty).Trim().ToLowerInvariant();
        if (normalized is not (StageThemeAuto or StageThemeLight or StageThemeDark))
        {
            normalized = StageThemeAuto;
        }

        bool changed = false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (!string.Equals(_stageWidgetTheme, normalized, StringComparison.Ordinal))
            {
                _stageWidgetTheme = normalized;
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaisePreferencesChanged();
        }
    }

    /// <summary>
    /// Controls Studio composer submit shortcut: false (default) = Ctrl+Enter sends (Enter inserts newline);
    /// true = Enter sends (Shift+Enter inserts newline).
    /// </summary>
    public bool SendOnEnter
    {
        get
        {
            lock (_gate)
            {
                EnsureLoadedLocked();
                return _sendOnEnter;
            }
        }
    }

    public void SetSendOnEnter(bool sendOnEnter)
    {
        bool changed = false;
        lock (_gate)
        {
            EnsureLoadedLocked();
            if (_sendOnEnter != sendOnEnter)
            {
                _sendOnEnter = sendOnEnter;
                SaveLocked();
                changed = true;
            }
        }

        if (changed)
        {
            RaisePreferencesChanged();
        }
    }

    public void CheckForExternalChangesAndNotify()
    {
        bool reloaded = false;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            long beforeTicks = _lastObservedWriteTicks;
            bool wasLoaded = _loaded;
            EnsureLoadedLocked();
            if (wasLoaded && _lastObservedWriteTicks != beforeTicks)
            {
                reloaded = true;
            }
        }

        if (reloaded)
        {
            RaisePreferencesChanged();
        }
    }

    private void TryStartWatcher()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (string.IsNullOrWhiteSpace(dir))
            {
                return;
            }

            Directory.CreateDirectory(dir);
            var watcher = new FileSystemWatcher(dir)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.CreationTime,
                IncludeSubdirectories = false
            };

            bool MatchesTargetFile(string? name) =>
                !string.IsNullOrWhiteSpace(name) &&
                string.Equals(name, _watchedFileName, StringComparison.OrdinalIgnoreCase);

            watcher.Changed += (_, e) =>
            {
                if (MatchesTargetFile(e.Name)) ScheduleExternalCheck();
            };
            watcher.Created += (_, e) =>
            {
                if (MatchesTargetFile(e.Name)) ScheduleExternalCheck();
            };
            watcher.Renamed += (_, e) =>
            {
                if (MatchesTargetFile(e.Name) || MatchesTargetFile(e.OldName)) ScheduleExternalCheck();
            };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch
        {
            // Ignore watcher setup failures on restricted paths.
        }
    }

    private void ScheduleExternalCheck()
    {
        long sinceSelfWrite = Environment.TickCount64 - Interlocked.Read(ref _lastSelfWriteTickMs);
        if (sinceSelfWrite is >= 0 and < 150)
        {
            return;
        }

        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _watcherDebounceCts?.Cancel();
            _watcherDebounceCts?.Dispose();
            cts = new CancellationTokenSource();
            _watcherDebounceCts = cts;
        }

        CancellationToken token = cts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                {
                    CheckForExternalChangesAndNotify();
                }
            }
            catch
            {
            }
        }, token);
    }

    private void RaisePreferencesChanged()
    {
        try
        {
            PreferencesChanged?.Invoke(this, EventArgs.Empty);
        }
        catch
        {
        }
    }

    private void EnsureLoadedLocked()
    {
        try
        {
            if (!File.Exists(_filePath))
            {
                _loaded = true;
                _lastObservedWriteTicks = 0;
                return;
            }

            long currentTicks = File.GetLastWriteTimeUtc(_filePath).Ticks;
            if (_loaded && currentTicks == _lastObservedWriteTicks)
            {
                return;
            }

            string json = File.ReadAllText(_filePath, Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(json))
            {
                using JsonDocument doc = JsonDocument.Parse(json);
                JsonElement root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("hasSeenPickElementTip", out JsonElement pickTipEl) &&
                        pickTipEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenPickElementTip = pickTipEl.GetBoolean();
                    }

                    if (root.TryGetProperty("hasSeenStudioChannelOnboardingV1", out JsonElement mcpGuideEl) &&
                        mcpGuideEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenAgentMcpGuide = mcpGuideEl.GetBoolean();
                    }

                    if (root.TryGetProperty("hasSeenDirectStepTour", out JsonElement directTourEl) &&
                        directTourEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenDirectStepTour = directTourEl.GetBoolean();
                    }

                    if (root.TryGetProperty("hasSeenAgentStepTour", out JsonElement agentTourEl) &&
                        agentTourEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _hasSeenAgentStepTour = agentTourEl.GetBoolean();
                    }

                    if (root.TryGetProperty("preferredStudioChannel", out JsonElement channelEl) &&
                        channelEl.ValueKind == JsonValueKind.String)
                    {
                        string rawCh = (channelEl.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                        _preferredStudioChannel = rawCh is ChannelDirect or ChannelAgentMcp
                            ? rawCh
                            : ChannelAuto;
                    }

                    if (root.TryGetProperty("locateOnDesktopAfterCommit", out JsonElement locateEl) &&
                        locateEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _locateOnDesktopAfterCommit = locateEl.GetBoolean();
                    }

                    if (root.TryGetProperty("preferredAiProviderId", out JsonElement providerEl) &&
                        providerEl.ValueKind == JsonValueKind.String)
                    {
                        _preferredAiProviderId = providerEl.GetString() ?? string.Empty;
                    }

                    if (root.TryGetProperty("preferredCodeEditor", out JsonElement editorEl) &&
                        editorEl.ValueKind == JsonValueKind.String)
                    {
                        string rawEd = (editorEl.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                        _preferredCodeEditor = rawEd is EditorCursor or EditorAntigravity or EditorVsCode or EditorTrae or EditorWindsurf
                            ? rawEd
                            : EditorAuto;
                    }

                    if (root.TryGetProperty("stageWidgetTheme", out JsonElement stageThemeEl) &&
                        stageThemeEl.ValueKind == JsonValueKind.String)
                    {
                        string rawTh = (stageThemeEl.GetString() ?? string.Empty).Trim().ToLowerInvariant();
                        _stageWidgetTheme = rawTh is StageThemeLight or StageThemeDark
                            ? rawTh
                            : StageThemeAuto;
                    }

                    if (root.TryGetProperty("sendOnEnter", out JsonElement sendOnEnterEl) &&
                        sendOnEnterEl.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        _sendOnEnter = sendOnEnterEl.GetBoolean();
                    }
                }
            }

            _loaded = true;
            _lastObservedWriteTicks = currentTicks;
        }
        catch
        {
            _loaded = true;
        }
    }

    private void SaveLocked()
    {
        try
        {
            string? dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                Directory.CreateDirectory(dir);
            }

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
            {
                writer.WriteStartObject();
                writer.WriteBoolean("hasSeenPickElementTip", _hasSeenPickElementTip);
                writer.WriteBoolean("hasSeenStudioChannelOnboardingV1", _hasSeenAgentMcpGuide);
                writer.WriteBoolean("hasSeenDirectStepTour", _hasSeenDirectStepTour);
                writer.WriteBoolean("hasSeenAgentStepTour", _hasSeenAgentStepTour);
                writer.WriteString("preferredStudioChannel", _preferredStudioChannel);
                writer.WriteBoolean("locateOnDesktopAfterCommit", _locateOnDesktopAfterCommit);
                writer.WriteString("preferredAiProviderId", _preferredAiProviderId);
                writer.WriteString("preferredCodeEditor", _preferredCodeEditor);
                writer.WriteString("stageWidgetTheme", _stageWidgetTheme);
                writer.WriteBoolean("sendOnEnter", _sendOnEnter);
                writer.WriteEndObject();
            }

            string content = Encoding.UTF8.GetString(stream.ToArray());
            Interlocked.Exchange(ref _lastSelfWriteTickMs, Environment.TickCount64);
            CustomWidgetStorageFileOps.ReplaceTextFileAtomically(_filePath, content);
            Interlocked.Exchange(ref _lastSelfWriteTickMs, Environment.TickCount64);
            if (File.Exists(_filePath))
            {
                _lastObservedWriteTicks = File.GetLastWriteTimeUtc(_filePath).Ticks;
            }
        }
        catch
        {
            // Ignore non-fatal preference persistence errors.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _watcherDebounceCts?.Cancel();
                _watcherDebounceCts?.Dispose();
            }
            catch
            {
            }

            _watcherDebounceCts = null;
        }

        if (_watcher is not null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Dispose();
            }
            catch
            {
            }

            _watcher = null;
        }
    }
}
