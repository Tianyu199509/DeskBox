using System.Globalization;
using System.Text;
using DeskBox.Contracts;
using DeskBox.Models;
using DeskBox.ViewModels;

namespace DeskBox.Services;

/// <summary>
/// Coordinates DropZone discovery, dropped/picked file validation and context construction,
/// safe text extraction, and session path authorization for <see cref="CustomWidgetViewModel"/>.
/// </summary>
internal sealed class CustomWidgetDropZoneCoordinator
{
    private readonly CustomWidgetViewModel _vm;

    internal CustomWidgetDropZoneCoordinator(CustomWidgetViewModel vm) => _vm = vm;

    internal static string TryUnwrapFileUriToLocalPath(string rawCandidate)
    {
        string trimmed = rawCandidate.Trim();
        if (trimmed.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(trimmed, UriKind.Absolute, out Uri? fileUri) &&
            fileUri.IsFile &&
            !string.IsNullOrWhiteSpace(fileUri.LocalPath))
        {
            return fileUri.LocalPath.Trim();
        }

        return trimmed;
    }

    internal string ResolveAuthorizedImagePath(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return string.Empty;
        }

        string trimmed = rawPath.Trim();
        if (CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri(trimmed) ||
            trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("ms-appx://", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        trimmed = TryUnwrapFileUriToLocalPath(trimmed);
        if (CustomWidgetNetworkSecurity.IsBlockedUncOrRemoteFileUri(trimmed) ||
            trimmed.Contains('%', StringComparison.Ordinal))
        {
            return string.Empty;
        }

        try
        {
            if (!Path.IsPathRooted(trimmed))
            {
                string normalizedRel = trimmed
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                string candidate = Path.GetFullPath(Path.Combine(_vm.Store.WidgetDirectory, normalizedRel));
                return _vm.Store.IsInsideWidgetDirectory(candidate) && File.Exists(candidate)
                    ? candidate
                    : string.Empty;
            }

            string fullPath = Path.GetFullPath(trimmed);
            if (!File.Exists(fullPath))
            {
                return string.Empty;
            }

            if (_vm.Store.IsInsideWidgetDirectory(fullPath) ||
                IsPathInsideDirectory(fullPath, CustomWidgetHostSnapshotService.GetSmtcCoverCacheDirectory()))
            {
                return fullPath;
            }

            if (IsAuthorizedByUserSettingsOrDroppedItems(fullPath))
            {
                return fullPath;
            }
        }
        catch
        {
        }

        return string.Empty;
    }

    internal bool IsAuthorizedByUserSettingsOrDroppedItems(string fullPath)
    {
        if (_vm.Store.IsInsideWidgetDirectory(fullPath) ||
            _vm.SessionAuthorizedPathsInternal.Contains(fullPath) ||
            IsPathInsideDirectory(fullPath, CustomWidgetHostSnapshotService.GetSmtcCoverCacheDirectory()))
        {
            return true;
        }

        foreach (KeyValuePair<string, string> kvp in _vm.SettingsInternal)
        {
            if (string.IsNullOrWhiteSpace(kvp.Value))
            {
                continue;
            }

            CustomWidgetSettingDefinition? def = _vm.ManifestInternal.Settings.FirstOrDefault(
                d => string.Equals(d.Key, kvp.Key, StringComparison.Ordinal));
            if (def is not null && string.Equals(def.DefaultValue, kvp.Value, StringComparison.Ordinal))
            {
                continue;
            }

            string[] lines = kvp.Value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (string rawLine in lines)
            {
                string candidate = rawLine;
                int pipeIdx = candidate.IndexOf('|');
                if (pipeIdx > 0)
                {
                    candidate = candidate[..pipeIdx].Trim();
                }

                if (string.IsNullOrWhiteSpace(candidate) ||
                    candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                    candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                candidate = TryUnwrapFileUriToLocalPath(candidate);
                string expanded = CustomWidgetOnboardingHelper.ExpandUserProfilePlaceholder(candidate);
                if (!Path.IsPathRooted(expanded))
                {
                    continue;
                }

                try
                {
                    string configuredFull = Path.GetFullPath(expanded);
                    if (string.Equals(fullPath, configuredFull, StringComparison.OrdinalIgnoreCase) ||
                        IsPathInsideDirectory(fullPath, configuredFull))
                    {
                        return true;
                    }
                }
                catch
                {
                }
            }
        }

        if (_vm.IsPermissionAllowed(CustomWidgetPermissionKind.LocalFileAccess))
        {
            foreach (List<CustomWidgetRecord> list in _vm.CollectionsInternal.Values)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    CustomWidgetRecord rec = list[i];
                    if (rec.Id.StartsWith("sample-", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (MatchesRecordAuthorizedPath(fullPath, rec.GetField("filePath")) ||
                        MatchesRecordAuthorizedPath(fullPath, rec.GetField("path")) ||
                        MatchesRecordAuthorizedPath(fullPath, rec.GetField("src")))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    internal static bool MatchesRecordAuthorizedPath(string fullPath, string? rawRecordPath)
    {
        if (string.IsNullOrWhiteSpace(rawRecordPath))
        {
            return false;
        }

        string unwrapped = TryUnwrapFileUriToLocalPath(rawRecordPath);
        if (!Path.IsPathRooted(unwrapped))
        {
            return false;
        }

        try
        {
            return string.Equals(fullPath, Path.GetFullPath(unwrapped), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    internal static bool IsPathInsideDirectory(string fullCandidate, string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        try
        {
            string root = Path.GetFullPath(directoryPath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return fullCandidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }


    internal bool HasAnyDropZoneNode() =>
        CustomWidgetViewModel.NodeTreeContainsType(_vm.ManifestInternal.View, "DropZone") ||
        _vm.ManifestInternal.Popovers.Any(static p => CustomWidgetViewModel.NodeTreeContainsType(p, "DropZone"));

    internal string? FindAnyDropZoneAccept()
    {
        string? accept = FindFirstDropZoneAccept(_vm.ManifestInternal.View);
        if (!string.IsNullOrWhiteSpace(accept))
        {
            return accept;
        }

        foreach (CustomWidgetNodeSpec pop in _vm.ManifestInternal.Popovers)
        {
            accept = FindFirstDropZoneAccept(pop);
            if (!string.IsNullOrWhiteSpace(accept))
            {
                return accept;
            }
        }

        return null;
    }

    internal string? FindAnyDropZoneStorageMode()
    {
        string? mode = FindFirstDropZoneStorageMode(_vm.ManifestInternal.View);
        if (!string.IsNullOrWhiteSpace(mode))
        {
            return mode;
        }

        foreach (CustomWidgetNodeSpec pop in _vm.ManifestInternal.Popovers)
        {
            mode = FindFirstDropZoneStorageMode(pop);
            if (!string.IsNullOrWhiteSpace(mode))
            {
                return mode;
            }
        }

        return null;
    }

    internal IReadOnlyList<CustomWidgetActionSpec> FindAnyDropZoneActions()
    {
        IReadOnlyList<CustomWidgetActionSpec> actions = FindFirstDropZoneActions(_vm.ManifestInternal.View);
        if (actions.Count > 0)
        {
            return actions;
        }

        foreach (CustomWidgetNodeSpec pop in _vm.ManifestInternal.Popovers)
        {
            actions = FindFirstDropZoneActions(pop);
            if (actions.Count > 0)
            {
                return actions;
            }
        }

        return [];
    }


    internal async Task<bool> ImportDroppedFilesAsync(
        IReadOnlyList<string> filePaths,
        bool forceCopyToPortableStore = false)
    {
        if (!_vm.AcceptsFileDrops || filePaths.Count == 0)
        {
            return false;
        }

        IReadOnlyList<CustomWidgetActionSpec> actions = _vm.ManifestInternal.OnDrop?.Actions is { Count: > 0 } rootDrop
            ? rootDrop
            : FindAnyDropZoneActions();
        if (actions.Count == 0)
        {
            return false;
        }

        bool usePortableStore = _vm.ManifestInternal.OnDrop is not null || _vm.HasDropZoneNode || forceCopyToPortableStore;
        string mode = forceCopyToPortableStore ? "copy" : _vm.EffectiveDropMode;
        int importedCount = 0;
        bool dropInvokesAiRun = CustomWidgetViewModel.ActionsContainAiRun(actions);

        var resolvedFiles = new List<CustomWidgetDropFileContext>(filePaths.Count);
        foreach (string rawPath in filePaths)
        {
            if (dropInvokesAiRun && resolvedFiles.Count >= CustomWidgetViewModel.MaxAiRunsPerDropBatch)
            {
                break;
            }

            CustomWidgetDropFileContext? dropCtx = await BuildDropFileContextAsync(
                rawPath,
                usePortableStore,
                forceCopyToPortableStore);
            if (dropCtx is not null)
            {
                resolvedFiles.Add(dropCtx);
            }
        }

        if (resolvedFiles.Count == 0)
        {
            return false;
        }

        bool batchViaScript = actions.Any(static a =>
            string.Equals(a.Type, "script.run", StringComparison.OrdinalIgnoreCase));
        if (batchViaScript)
        {
            try
            {
                CustomWidgetEvaluationContext evalContext = _vm.CreateEvaluationContext().WithDrop(resolvedFiles[0], resolvedFiles);
                await _vm.ExecuteActionsAsync(actions, evalContext);
                importedCount = resolvedFiles.Count;
            }
            catch (Exception ex)
            {
                App.Log($"[CustomWidget] Failed to handle dropped batch via script: {ex.Message}");
            }
        }
        else
        {
            foreach (CustomWidgetDropFileContext dropCtx in resolvedFiles)
            {
                try
                {
                    CustomWidgetEvaluationContext evalContext = _vm.CreateEvaluationContext().WithDrop(dropCtx, resolvedFiles);
                    await _vm.ExecuteActionsAsync(actions, evalContext);
                    importedCount++;
                }
                catch (Exception ex)
                {
                    App.Log($"[CustomWidget] Failed to import dropped file '{dropCtx.Path}': {ex.Message}");
                }
            }
        }

        if (importedCount > 0 && usePortableStore)
        {
            _vm.RaiseFeedback(
                mode == "copy"
                    ? _vm.LocalizationServiceInternal.Format("CustomWidget.Toast.FilesCopied", importedCount)
                    : _vm.LocalizationServiceInternal.Format("CustomWidget.Toast.FilesLinked", importedCount),
                WidgetFeedbackSeverity.Info);
        }

        return importedCount > 0;
    }

    internal async Task<bool> HandleDropZoneFilesAsync(
        IReadOnlyList<string> filePaths,
        IReadOnlyList<CustomWidgetActionSpec> nodeDropActions,
        bool forceCopyToPortableStore = false,
        string? nodeDropMode = null)
    {
        if (_vm.IsDisposedInternal || filePaths.Count == 0)
        {
            return false;
        }

        IReadOnlyList<CustomWidgetActionSpec> effectiveActions = nodeDropActions.Count > 0
            ? nodeDropActions
            : (_vm.ManifestInternal.OnDrop?.Actions ?? []);
        if (effectiveActions.Count == 0)
        {
            return false;
        }

        bool usePortableStore = nodeDropActions.Count > 0 ||
            _vm.ManifestInternal.OnDrop is not null ||
            forceCopyToPortableStore;
        string? evaluatedNodeMode = string.IsNullOrWhiteSpace(nodeDropMode)
            ? null
            : CustomWidgetExpressionEvaluator.Interpolate(nodeDropMode, _vm.CreateEvaluationContext());
        bool nodeRequestsCopy = string.Equals(evaluatedNodeMode, "copy", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(evaluatedNodeMode, "auto", StringComparison.OrdinalIgnoreCase);

        var resolvedFiles = new List<CustomWidgetDropFileContext>(filePaths.Count);
        foreach (string rawPath in filePaths)
        {
            CustomWidgetDropFileContext? dropCtx = await BuildDropFileContextAsync(
                rawPath,
                usePortableStore,
                forceCopyToPortableStore || nodeRequestsCopy);
            if (dropCtx is not null)
            {
                resolvedFiles.Add(dropCtx);
            }
        }

        if (resolvedFiles.Count == 0)
        {
            return false;
        }

        bool batchViaScript = effectiveActions.Any(static a =>
            string.Equals(a.Type, "script.run", StringComparison.OrdinalIgnoreCase));
        if (batchViaScript)
        {
            CustomWidgetEvaluationContext evalContext = _vm.CreateEvaluationContext().WithDrop(resolvedFiles[0], resolvedFiles);
            await _vm.ExecuteActionsAsync(effectiveActions, evalContext);
            return true;
        }

        int processed = 0;
        foreach (CustomWidgetDropFileContext dropCtx in resolvedFiles)
        {
            CustomWidgetEvaluationContext evalContext = _vm.CreateEvaluationContext().WithDrop(dropCtx, resolvedFiles);
            await _vm.ExecuteActionsAsync(effectiveActions, evalContext);
            processed++;
        }

        return processed > 0;
    }

    internal async Task<bool> HandleStructuredDropAsync(
        CustomWidgetStructuredDropPayload payload,
        IReadOnlyList<CustomWidgetActionSpec>? nodeDropActions = null,
        string? nodeDropMode = null,
        string? nodeAcceptSpec = null)
    {
        if (_vm.IsDisposedInternal || payload is null)
        {
            return false;
        }

        string effectiveAccept = !string.IsNullOrWhiteSpace(nodeAcceptSpec)
            ? nodeAcceptSpec!
            : _vm.EffectiveDropAccept;

        if (string.Equals(payload.Kind, "files", StringComparison.OrdinalIgnoreCase))
        {
            if (payload.FilePaths is not { Count: > 0 } ||
                !CustomWidgetDropAcceptHelper.AllowsFiles(effectiveAccept))
            {
                return false;
            }

            return nodeDropActions is { Count: > 0 } || !string.IsNullOrWhiteSpace(nodeDropMode)
                ? await HandleDropZoneFilesAsync(
                    payload.FilePaths,
                    nodeDropActions ?? [],
                    forceCopyToPortableStore: payload.ForceCopyFiles,
                    nodeDropMode: nodeDropMode)
                : await ImportDroppedFilesAsync(
                    payload.FilePaths,
                    forceCopyToPortableStore: payload.ForceCopyFiles);
        }

        CustomWidgetDropAcceptHelper.ParseAcceptModes(
            effectiveAccept,
            out _,
            out bool allowsText,
            out bool allowsItems,
            out _);

        bool isItemDrop = string.Equals(payload.Kind, "todo", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(payload.Kind, "quickCapture", StringComparison.OrdinalIgnoreCase);
        if (isItemDrop && !allowsItems && !allowsText)
        {
            return false;
        }

        if (string.Equals(payload.Kind, "text", StringComparison.OrdinalIgnoreCase) && !allowsText)
        {
            return false;
        }

        IReadOnlyList<CustomWidgetActionSpec> actions = nodeDropActions is { Count: > 0 }
            ? nodeDropActions
            : (_vm.ManifestInternal.OnDrop?.Actions is { Count: > 0 } rootActions
                ? rootActions
                : FindFirstDropZoneActions(_vm.ManifestInternal.View));

        if (actions.Count == 0 &&
            _vm.CompiledScriptInternal is not null &&
            _vm.CompiledScriptInternal.ExportedFunctions.Contains("onDrop"))
        {
            actions = [new CustomWidgetActionSpec("script.run", Fn: "onDrop")];
        }

        if (actions.Count == 0)
        {
            return false;
        }

        bool batchViaScript = actions.Any(static a =>
            string.Equals(a.Type, "script.run", StringComparison.OrdinalIgnoreCase));
        if (batchViaScript || payload.Items is not { Count: > 1 })
        {
            CustomWidgetRecord? singleRecord = payload.Items is { Count: > 0 } itemsList
                ? BuildDropRecordFromItem(itemsList[0])
                : null;
            CustomWidgetEvaluationContext evalContext = _vm.CreateEvaluationContext()
                .WithStructuredDrop(payload.Kind, payload.Text, payload.Items);
            if (singleRecord is not null)
            {
                evalContext = evalContext.WithItem(singleRecord, 0);
            }

            await _vm.ExecuteActionsAsync(actions, evalContext);
            return true;
        }

        int handledItems = 0;
        for (int i = 0; i < payload.Items.Count; i++)
        {
            CustomWidgetDropItemRecord item = payload.Items[i];
            CustomWidgetRecord itemRecord = BuildDropRecordFromItem(item);
            string itemText = !string.IsNullOrEmpty(item.Text) ? item.Text : item.Title;
            CustomWidgetEvaluationContext evalContext = _vm.CreateEvaluationContext()
                .WithStructuredDrop(payload.Kind, itemText, [item])
                .WithItem(itemRecord, i);
            await _vm.ExecuteActionsAsync(actions, evalContext);
            handledItems++;
        }

        return handledItems > 0;
    }

    internal static CustomWidgetRecord BuildDropRecordFromItem(CustomWidgetDropItemRecord item)
    {
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["title"] = item.Title ?? string.Empty,
            ["text"] = item.Text ?? string.Empty,
            ["body"] = item.Body ?? string.Empty,
            ["url"] = item.Url ?? string.Empty,
            ["completed"] = item.IsCompleted ? "true" : "false",
            ["source"] = item.Source ?? string.Empty
        };
        return new CustomWidgetRecord
        {
            Id = !string.IsNullOrWhiteSpace(item.Id) ? item.Id : Guid.NewGuid().ToString("N")[..12],
            Fields = fields
        };
    }

    internal async Task<CustomWidgetDropFileContext?> BuildDropFileContextAsync(
        string? rawPath,
        bool usePortableStoreMode,
        bool forceCopyToPortableStore = false)
    {
        if (string.IsNullOrWhiteSpace(rawPath) ||
            (!File.Exists(rawPath) && !Directory.Exists(rawPath)))
        {
            return null;
        }

        try
        {
            bool isDirectory = Directory.Exists(rawPath) && !File.Exists(rawPath);
            bool mustCopyTemporary = forceCopyToPortableStore ||
                Platform.DropStagingFileOps.IsUnderVirtualDropsDirectory(rawPath);
            string mode = mustCopyTemporary ? "copy" : _vm.EffectiveDropMode;
            bool effectivePortableStore = usePortableStoreMode || mustCopyTemporary;
            (string storedPath, string fullPath, string fileName, string actualMode) =
                (_vm.IsPreviewOnlyInternal || !effectivePortableStore)
                    ? (
                        Path.GetFullPath(rawPath),
                        Path.GetFullPath(rawPath),
                        Path.GetFileName(rawPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                        "link")
                    : await _vm.Store.ResolveDroppedFileWithPortablePathAsync(rawPath, mode);

            if (!string.IsNullOrWhiteSpace(fullPath))
            {
                _vm.SessionAuthorizedPathsInternal.Add(fullPath);
            }

            string ext = isDirectory ? string.Empty : Path.GetExtension(fileName).TrimStart('.').ToLowerInvariant();
            string sizeText = isDirectory ? "Folder" : string.Empty;
            long sizeBytes = 0;
            string modifiedIso = string.Empty;
            string extractedText = string.Empty;
            if (File.Exists(fullPath))
            {
                var fi = new FileInfo(fullPath);
                sizeBytes = Math.Max(0, fi.Length);
                sizeText = CustomWidgetLocalFileEngine.FormatByteSize(sizeBytes);
                modifiedIso = fi.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
                extractedText = await TryReadSafeDropTextAsync(fullPath, ext);
            }
            else if (Directory.Exists(fullPath))
            {
                var di = new DirectoryInfo(fullPath);
                modifiedIso = di.LastWriteTimeUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
            }

            return new CustomWidgetDropFileContext
            {
                Name = fileName,
                Path = storedPath,
                Extension = ext,
                SizeText = sizeText,
                SizeBytes = sizeBytes,
                ModifiedIso = modifiedIso,
                Mode = actualMode,
                Text = extractedText
            };
        }
        catch (Exception ex)
        {
            App.Log($"[CustomWidget] Failed to inspect dropped/picked file '{rawPath}': {ex.Message}");
            return null;
        }
    }

    internal static string? FindFirstDropZoneStorageMode(CustomWidgetNodeSpec node) =>
        node.EnumerateAllNodes()
            .FirstOrDefault(static n =>
                string.Equals(n.Type, "DropZone", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(n.StorageMode))
            ?.StorageMode;

    internal static IReadOnlyList<CustomWidgetActionSpec> FindFirstDropZoneActions(CustomWidgetNodeSpec node) =>
        node.EnumerateAllNodes()
            .FirstOrDefault(static n =>
                string.Equals(n.Type, "DropZone", StringComparison.OrdinalIgnoreCase) &&
                n.OnDropActions.Count > 0)
            ?.OnDropActions ?? [];

    internal static string? FindFirstDropZoneAccept(CustomWidgetNodeSpec node) =>
        node.EnumerateAllNodes()
            .FirstOrDefault(static n =>
                string.Equals(n.Type, "DropZone", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(n.Accept))
            ?.Accept;

    internal static bool AnyDropZoneAllowsFiles(CustomWidgetNodeSpec node, string? rootAccept)
    {
        foreach (CustomWidgetNodeSpec candidate in node.EnumerateAllNodes())
        {
            if (string.Equals(candidate.Type, "DropZone", StringComparison.OrdinalIgnoreCase))
            {
                string? effectiveAccept = !string.IsNullOrWhiteSpace(candidate.Accept) ? candidate.Accept : rootAccept;
                if (CustomWidgetDropAcceptHelper.AllowsFiles(effectiveAccept))
                {
                    return true;
                }
            }
        }

        return false;
    }

    internal static bool DoesManifestAcceptFileDrops(CustomWidgetManifest? manifest)
    {
        if (manifest is null)
        {
            return false;
        }

        if (manifest.OnDrop is { } drop && CustomWidgetDropAcceptHelper.AllowsFiles(drop.Accept))
        {
            return true;
        }

        if (AnyDropZoneAllowsFiles(manifest.View, manifest.OnDrop?.Accept))
        {
            return true;
        }

        foreach (CustomWidgetNodeSpec popover in manifest.Popovers)
        {
            if (AnyDropZoneAllowsFiles(popover, manifest.OnDrop?.Accept))
            {
                return true;
            }
        }

        return false;
    }

    internal static bool DoesManifestJsonAcceptFileDrops(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return false;
        }

        CustomWidgetParseResult parsed = CustomWidgetManifestParser.Parse(rawJson);
        return DoesManifestAcceptFileDrops(parsed.Manifest);
    }


    internal static readonly System.Collections.Frozen.FrozenSet<string> SupportedDropTextExtensions =
        System.Collections.Frozen.FrozenSet.ToFrozenSet(
        [
            "txt", "md", "markdown", "csv", "tsv", "json", "log", "yml", "yaml", "xml", "ini", "toml"
        ],
        StringComparer.OrdinalIgnoreCase);

    internal static bool IsSupportedDropTextExtension(string? extension)
    {
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        string clean = extension.Trim().TrimStart('.');
        return SupportedDropTextExtensions.Contains(clean);
    }

    internal static async Task<string> TryReadSafeDropTextAsync(string fullPath, string? extension)
    {
        string ext = string.IsNullOrWhiteSpace(extension)
            ? Path.GetExtension(fullPath)
            : extension;
        if (!IsSupportedDropTextExtension(ext) ||
            !File.Exists(fullPath) ||
            DeskBox.Platform.DriveMediaInterop.HasUnhydratedCloudAttributesOnDisk(fullPath))
        {
            return string.Empty;
        }

        try
        {
            byte[] buffer = new byte[CustomWidgetViewModel.MaxDropReadBytes];
            int totalRead = 0;
            await using (var fs = new FileStream(
                             fullPath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.ReadWrite | FileShare.Delete,
                             bufferSize: 4096,
                             useAsync: true))
            {
                while (totalRead < buffer.Length)
                {
                    int n = await fs.ReadAsync(buffer.AsMemory(totalRead, buffer.Length - totalRead));
                    if (n <= 0)
                    {
                        break;
                    }

                    totalRead += n;
                }
            }

            if (totalRead == 0)
            {
                return string.Empty;
            }

            ReadOnlySpan<byte> span = buffer.AsSpan(0, totalRead);
            string decoded;
            if (span.Length >= 2 && span[0] == 0xFF && span[1] == 0xFE)
            {
                int evenLen = (span.Length - 2) & ~1;
                decoded = Encoding.Unicode.GetString(span.Slice(2, evenLen));
            }
            else if (span.Length >= 2 && span[0] == 0xFE && span[1] == 0xFF)
            {
                int evenLen = (span.Length - 2) & ~1;
                decoded = Encoding.BigEndianUnicode.GetString(span.Slice(2, evenLen));
            }
            else
            {
                if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
                {
                    span = span[3..];
                }

                // Reject binary payloads masquerading under text extensions.
                if (span.IndexOf((byte)0) >= 0)
                {
                    return string.Empty;
                }

                decoded = Encoding.UTF8.GetString(span);
            }

            string normalized = decoded
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Trim();

            return normalized.Length > CustomWidgetViewModel.MaxDropTextChars
                ? normalized[..CustomWidgetViewModel.MaxDropTextChars]
                : normalized;
        }
        catch
        {
            return string.Empty;
        }
    }

}
