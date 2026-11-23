using DeskBox.Helpers;
using DeskBox.Platform;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace DeskBox.Views;

public sealed partial class OnboardingWindow
{
    private const string StatusInfoGlyph = "\uE946";
    private const string StatusCompleteGlyph = "\uE73E";
    private const string StatusHiddenGlyph = "\uE890";
    private const string StatusVisibleGlyph = "\uE8A7";

    private int _summonToggleCount;
    private bool _isSynchronizingDoubleClick;
    private bool _isSynchronizingSearchHotkey;
    private bool _isChangingStoragePath;
    private readonly Dictionary<WidgetKind, bool> _featureTileStates = new();

    // ════════════════════════════════════════════════════════════
    //  Step 2: moving changes the path. Quick drive switching and
    //  custom folder selection work in place; drag-drop mode stays
    //  adjustable later in Settings.
    // ════════════════════════════════════════════════════════════

    private void SetupDropStep()
    {
        RefreshDropStepPathAndDrives();
        _dropSceneShowsCopy = IsCurrentDropModeCopy();
    }

    private void RefreshDropStepPathAndDrives()
    {
        // Slice reads (never the AppSettings facade passthroughs — the
        // facade-access contract test pins this file's budget).
        string root = CurrentManagedStorageRoot();
        string subfolder = _localizationService.T("Widget.ManagedFolderBaseName");
        string fullPath = System.IO.Path.Combine(root, subfolder);
        Step2PathValue.Text = fullPath;
        ToolTipService.SetToolTip(Step2PathValue, fullPath);

        string fileName = _localizationService.T("Onboarding.Scene.FileName1");
        string desktopTail = PathTail(Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
        ScenePathOldText.Text = $"…\\{(string.IsNullOrWhiteSpace(desktopTail) ? "Desktop" : desktopTail)}\\{fileName}";
        ScenePathChipText.Text = FormatSceneStorageChipPath(root, fullPath, subfolder, fileName);

        RefreshStep2DriveButtons(root);
    }

    private static string FormatSceneStorageChipPath(
        string root,
        string fullPath,
        string subfolder,
        string fileName)
    {
        string? pathRoot = System.IO.Path.GetPathRoot(root);
        string? parentDir = System.IO.Path.GetDirectoryName(
            System.IO.Path.TrimEndingDirectorySeparator(root));
        if (!string.IsNullOrWhiteSpace(pathRoot) &&
            !string.IsNullOrWhiteSpace(parentDir) &&
            string.Equals(
                System.IO.Path.TrimEndingDirectorySeparator(parentDir),
                System.IO.Path.TrimEndingDirectorySeparator(pathRoot),
                StringComparison.OrdinalIgnoreCase))
        {
            return System.IO.Path.Combine(fullPath, fileName);
        }

        string rootTail = PathTail(root);
        string drivePrefix = string.IsNullOrWhiteSpace(pathRoot)
            ? string.Empty
            : pathRoot.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(rootTail))
        {
            return System.IO.Path.Combine(fullPath, fileName);
        }

        return string.IsNullOrWhiteSpace(drivePrefix)
            ? $"…\\{rootTail}\\{subfolder}\\{fileName}"
            : $"{drivePrefix}\\…\\{rootTail}\\{subfolder}\\{fileName}";
    }

    private void RefreshStep2DriveButtons(string currentRoot)
    {
        if (Step2DriveButtonsHost is null || Step2DriveStrip is null)
        {
            return;
        }

        IReadOnlyList<ManagedStorageSelectableDriveOption> options =
            ManagedStoragePathService.GetSelectableStorageDriveOptions();
        Step2DriveButtonsHost.Children.Clear();
        if (options.Count < 2)
        {
            Step2DriveStrip.Visibility = Visibility.Collapsed;
            return;
        }

        foreach (ManagedStorageSelectableDriveOption option in options)
        {
            bool isSelected = ManagedStorageMigrationService.Same(currentRoot, option.TargetRootPath);
            var button = new ToggleButton
            {
                Content = $"{option.DriveLabel} · {FormatDriveFreeSpace(option.AvailableFreeSpace)}",
                Tag = option.TargetRootPath,
                IsChecked = isSelected,
                IsEnabled = !_isChangingStoragePath,
                MinHeight = 30,
                Padding = new Thickness(11, 4, 11, 4),
                CornerRadius = new CornerRadius(6)
            };
            ToolTipService.SetToolTip(button, option.TargetRootPath);
            AutomationProperties.SetName(button, $"{option.DriveLabel} {option.TargetRootPath}");
            button.Click += Step2DriveButton_Click;
            Step2DriveButtonsHost.Children.Add(button);
        }

        Step2DriveStrip.Visibility = Visibility.Visible;
    }

    private static string FormatDriveFreeSpace(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = Math.Max(0, bytes);
        int unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }

        if (unit == 0)
        {
            return $"{bytes} {units[0]}";
        }

        return size >= 10
            ? $"{size:0} {units[unit]}"
            : $"{size:0.#} {units[unit]}";
    }

    private void SetStoragePathControlsEnabled(bool enabled)
    {
        Step2OpenFolderButton.IsEnabled = enabled;
        Step2PathChangeButton.IsEnabled = enabled;
        foreach (UIElement child in Step2DriveButtonsHost.Children)
        {
            if (child is Control control)
            {
                control.IsEnabled = enabled;
            }
        }
    }

    private string CurrentManagedStorageRoot() =>
        SettingsService.NormalizeManagedStorageRootPath(
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath);

    private static string PathTail(string path) =>
        System.IO.Path.GetFileName(path.TrimEnd(
            System.IO.Path.DirectorySeparatorChar,
            System.IO.Path.AltDirectorySeparatorChar));

    /// <summary>
    /// Returns whether the persisted unified drag-drop preference is Copy so
    /// the Step 2 stage animation mirrors external Settings changes while open.
    /// </summary>
    private bool IsCurrentDropModeCopy() =>
        string.Equals(
            _settingsService.Settings.FileWidget.ManagedDropAction,
            SettingsService.ManagedDropActionCopy,
            StringComparison.Ordinal);

    private void Step2OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string root = CurrentManagedStorageRoot();
            System.IO.Directory.CreateDirectory(root);
            Services.ShellLaunchService.FireAndForgetOpenPath(root);
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Failed to open managed storage root: {ex.Message}");
        }
    }

    private void Step2DriveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isChangingStoragePath || sender is not ToggleButton { Tag: string targetRoot })
        {
            RefreshStep2DriveButtons(CurrentManagedStorageRoot());
            return;
        }

        string currentRoot = CurrentManagedStorageRoot();
        if (ManagedStorageMigrationService.Same(currentRoot, targetRoot))
        {
            RefreshStep2DriveButtons(currentRoot);
            return;
        }

        RefreshStep2DriveButtons(currentRoot);
        _storagePathUpdateTask = ApplyOnboardingStorageRootAfterAsync(
            _storagePathUpdateTask,
            targetRoot);
    }

    private void Step2PathChange_Click(object sender, RoutedEventArgs e)
    {
        if (_isChangingStoragePath)
        {
            return;
        }

        _storagePathUpdateTask = PickAndApplyOnboardingStorageRootAfterAsync(
            _storagePathUpdateTask);
    }

    private async Task ApplyOnboardingStorageRootAfterAsync(
        Task previousTask,
        string targetRootPath)
    {
        await previousTask;
        if (_isClosed)
        {
            return;
        }

        _isChangingStoragePath = true;
        SetStoragePathControlsEnabled(false);
        try
        {
            await ApplyOnboardingStorageRootCoreAsync(targetRootPath);
        }
        finally
        {
            _isChangingStoragePath = false;
            if (!_isClosed)
            {
                SetStoragePathControlsEnabled(true);
                RefreshDropStepPathAndDrives();
            }
        }
    }

    private async Task PickAndApplyOnboardingStorageRootAfterAsync(Task previousTask)
    {
        await previousTask;
        if (_isClosed)
        {
            return;
        }

        _isChangingStoragePath = true;
        SetStoragePathControlsEnabled(false);
        try
        {
            while (!_isClosed)
            {
                string? folderPath = await FolderPickerService.PickFolderAsync(_hWnd);
                if (_isClosed || string.IsNullOrWhiteSpace(folderPath))
                {
                    return;
                }

                string scopedPath = ManagedStoragePathService.ResolvePickedStorageRootPath(
                    folderPath,
                    ManagedStorageMigrationService.TemporaryFolderName);
                bool chooseAnother = await ApplyOnboardingStorageRootCoreAsync(scopedPath);
                if (!chooseAnother)
                {
                    return;
                }
            }
        }
        finally
        {
            _isChangingStoragePath = false;
            if (!_isClosed)
            {
                SetStoragePathControlsEnabled(true);
                RefreshDropStepPathAndDrives();
            }
        }
    }

    private async Task<bool> ApplyOnboardingStorageRootCoreAsync(string candidatePath)
    {
        string normalizedPath = SettingsService.NormalizeManagedStorageRootPath(candidatePath);
        string currentRoot = CurrentManagedStorageRoot();
        if (ManagedStorageMigrationService.Same(normalizedPath, currentRoot))
        {
            return false;
        }

        if (ManagedStoragePathService.OverlapsDesktop(normalizedPath))
        {
            await ShowOnboardingDesktopOverlapDialogAsync();
            return true;
        }

        ManagedStorageRootValidationResult validation =
            ManagedStoragePathService.ValidateStorageRootCandidate(
                normalizedPath,
                AppDistributionService.Current.IsMicrosoftStore);
        if (!validation.IsValid)
        {
            if (validation.Problem == ManagedStorageRootValidationProblem.DesktopOverlap)
            {
                await ShowOnboardingDesktopOverlapDialogAsync();
            }
            else
            {
                await ShowOnboardingStorageProblemDialogAsync(StorageMigrationProblem.Unsupported);
            }

            return true;
        }

        if (_widgetManager is null)
        {
            _settingsService.Settings.FileWidget.DefaultManagedStorageRootPath = normalizedPath;
            _settingsService.SaveDebounced();
            RefreshDropStepPathAndDrives();
            return false;
        }

        try
        {
            ManagedStorageMigrationResult? emptySwitch =
                await _widgetManager.TrySwitchEmptyManagedStorageRootAsync(normalizedPath);
            if (emptySwitch is not null)
            {
                await TrySyncOnboardingDesktopShortcutAsync(emptySwitch.OldRootPath);
                RefreshDropStepPathAndDrives();
                return false;
            }
        }
        catch (Exception ex)
        {
            StorageMigrationProblem problem = ManagedStorageMigrationPresentation.Classify(ex);
            App.Log($"[Onboarding] Storage root switch failed ({problem}): {ex}");
            await ShowOnboardingStorageProblemDialogAsync(problem);
            return ManagedStorageMigrationPresentation.NeedsNewLocation(problem);
        }

        if (_isClosed || RootGrid.XamlRoot is null)
        {
            return false;
        }

        StorageMigrationConfirmOutcome? confirm = await ManagedStorageMigrationDialog.ConfirmAsync(
            RootGrid.XamlRoot,
            _localizationService,
            _widgetManager.GetDefaultManagedStorageWidgetCount(),
            currentRoot,
            normalizedPath,
            token => _widgetManager.PreviewManagedStorageMigrationAsync(normalizedPath, token),
            token => _widgetManager.CleanupInterruptedMigrationCopiesAsync(normalizedPath, token));
        if (confirm is null)
        {
            return false;
        }

        if (confirm.Choice == StorageMigrationStartChoice.ChooseLocation)
        {
            return true;
        }

        StorageMigrationDialogResult outcome = await ManagedStorageMigrationDialog.RunAsync(
            RootGrid.XamlRoot,
            RootGrid.DispatcherQueue,
            _localizationService,
            currentRoot,
            normalizedPath,
            options => _widgetManager.UpdateDefaultManagedStorageRootAsync(normalizedPath, options),
            restart: confirm.Choice == StorageMigrationStartChoice.Restart,
            retainOriginals: confirm.RetainOriginals,
            includeOrphanFolders: confirm.IncludeOrphanFolders,
            retrySourceCleanup: token => _widgetManager.RetryManagedStorageSourceCleanupAsync(normalizedPath, token),
            cleanupInterruptedCopies: token => _widgetManager.CleanupInterruptedMigrationCopiesAsync(normalizedPath, token));
        if (outcome.ChooseLocation)
        {
            return true;
        }

        if (outcome.Migration is { } migrated)
        {
            await TrySyncOnboardingDesktopShortcutAsync(migrated.OldRootPath);
            RefreshDropStepPathAndDrives();
        }

        return false;
    }

    private async Task TrySyncOnboardingDesktopShortcutAsync(string oldRootPath)
    {
        if (_desktopShortcutService is null)
        {
            return;
        }

        try
        {
            await _desktopShortcutService.SyncAsync(oldRootPath);
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Desktop shortcut sync deferred: {ex.Message}");
        }
    }

    private async Task ShowOnboardingDesktopOverlapDialogAsync()
    {
        if (_isClosed || RootGrid.XamlRoot is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(RootGrid),
            Title = _localizationService.T("Settings.ManagedPath.Error.DesktopOverlapTitle"),
            CloseButtonText = _localizationService.T("Common.Close"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.T("Settings.ManagedPath.Error.DesktopOverlapBody"),
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }

    private async Task ShowOnboardingStorageProblemDialogAsync(StorageMigrationProblem problem)
    {
        if (_isClosed || RootGrid.XamlRoot is null)
        {
            return;
        }

        string messageKey = GetMigrationProblemMessageKey(problem);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = NeutralInteractionBrush.ResolveEffectiveElementTheme(RootGrid),
            Title = _localizationService.T("Settings.Dialog.MigrateFailedTitle"),
            CloseButtonText = _localizationService.T("Common.Close"),
            DefaultButton = ContentDialogButton.Close,
            Content = new TextBlock
            {
                Text = _localizationService.T(messageKey),
                TextWrapping = TextWrapping.Wrap
            }
        };

        await dialog.ShowAsync();
    }

    private static string GetMigrationProblemMessageKey(StorageMigrationProblem problem) => problem switch
    {
        StorageMigrationProblem.CopiesRemoved => "Settings.Dialog.SafeMigrationError.CopiesRemoved",
        StorageMigrationProblem.DestinationChanged => "Settings.Dialog.SafeMigrationError.DestinationChanged",
        StorageMigrationProblem.DestinationConflict => "Settings.Dialog.SafeMigrationError.DestinationConflict",
        StorageMigrationProblem.RootsOverlap => "Settings.Dialog.SafeMigrationError.RootsOverlap",
        StorageMigrationProblem.VolumeUnavailable => "Settings.Dialog.SafeMigrationError.VolumeUnavailable",
        StorageMigrationProblem.SourceChanged => "Settings.Dialog.SafeMigrationError.SourceChanged",
        StorageMigrationProblem.Unsupported => "Settings.Dialog.SafeMigrationError.Unsupported",
        StorageMigrationProblem.InUse => "Settings.Dialog.SafeMigrationError.InUse",
        StorageMigrationProblem.AccessDenied => "Settings.Dialog.SafeMigrationError.AccessDenied",
        StorageMigrationProblem.DiskFull => "Settings.Dialog.SafeMigrationError.DiskFull",
        _ => "Settings.Dialog.SafeMigrationError.Unknown"
    };

    // ════════════════════════════════════════════════════════════
    //  Step 3: summon / dismiss (live, wired to the real toggle path)
    // ════════════════════════════════════════════════════════════

    private void SetupSummonStep()
    {
        string hotkeyText = GlobalHotkeyService.FormatActivation(
            GlobalHotkeyService.NormalizeActivation(
                _settingsService.Settings.GlobalHotkeyActivationKind,
                _settingsService.Settings.GlobalHotkeyModifiers,
                _settingsService.Settings.GlobalHotkeyKey),
            _localizationService);
        Step3BodyText.Text = _localizationService.Format(
            "Onboarding.Step3.Body",
            hotkeyText);
        SceneKeycapText.Text = hotkeyText;
        RefreshSummonStatus();
        SynchronizeDoubleClickToggle();
        RefreshSearchHotkeyStepControls();
    }

    private SearchHotkeyService? ResolveSearchHotkeyService()
    {
        EnsureSearchHotkeySubscription();
        return _observedSearchHotkeyService;
    }

    private void RefreshSearchHotkeyStepControls()
    {
        if (Step3SearchHotkeyBadgeText is null || Step3SearchHotkeyToggle is null)
        {
            return;
        }

        SearchHotkeyService? hotkeyService = ResolveSearchHotkeyService();
        bool enabled = _settingsService.Settings.Search.SearchHotkeyEnabled;
        string searchHotkeyText = GlobalHotkeyService.FormatActivation(
            GlobalHotkeyService.NormalizeActivation(
                _settingsService.Settings.Search.SearchHotkeyActivationKind,
                _settingsService.Settings.Search.SearchHotkeyModifiers,
                _settingsService.Settings.Search.SearchHotkeyKey),
            _localizationService);
        Step3SearchHotkeyBadgeText.Text = searchHotkeyText;
        string changeLabel = _localizationService.T("Onboarding.Step3.ChangeHotkey");

        bool hasConflict = enabled && hotkeyService is { IsRegistered: false };
        if (Step3SearchHotkeyWarningIcon is not null)
        {
            Step3SearchHotkeyWarningIcon.Visibility = hasConflict
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        if (Step3SearchHotkeyBadgeButton is not null)
        {
            Step3SearchHotkeyBadgeButton.Background = NeutralInteractionBrush.ResolveThemedResource(
                hasConflict ? "SystemFillColorCautionBackgroundBrush" : "SubtleFillColorSecondaryBrush",
                RootGrid);
            ToolTipService.SetToolTip(Step3SearchHotkeyBadgeButton, $"{searchHotkeyText} · {changeLabel}");
        }

        if (Step3SearchHotkeyBodyText is not null)
        {
            if (hasConflict)
            {
                string conflictReason = !string.IsNullOrWhiteSpace(hotkeyService!.LastError)
                    ? hotkeyService.LastError
                    : _localizationService.T("Settings.GlobalHotkey.Status.Conflict");
                Step3SearchHotkeyBodyText.Text = $"{conflictReason}（{changeLabel}）";
                Step3SearchHotkeyBodyText.Foreground = NeutralInteractionBrush.ResolveThemedResource(
                    "SystemFillColorCautionBrush",
                    RootGrid);
            }
            else
            {
                Step3SearchHotkeyBodyText.Text = _localizationService.T("Onboarding.Step3.SearchBody");
                Step3SearchHotkeyBodyText.Foreground = NeutralInteractionBrush.ResolveThemedResource(
                    "TextFillColorSecondaryBrush",
                    RootGrid);
            }
        }

        SynchronizeSearchHotkeyToggle();
    }

    private void SynchronizeSearchHotkeyToggle()
    {
        if (Step3SearchHotkeyToggle is null)
        {
            return;
        }

        _isSynchronizingSearchHotkey = true;
        try
        {
            Step3SearchHotkeyToggle.IsOn = _settingsService.Settings.Search.SearchHotkeyEnabled;
        }
        finally
        {
            _isSynchronizingSearchHotkey = false;
        }
    }

    private void Step3SearchHotkeyToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingSearchHotkey)
        {
            return;
        }

        bool requested = Step3SearchHotkeyToggle.IsOn;
        if (ResolveSearchHotkeyService() is { } hotkeyService)
        {
            hotkeyService.SetEnabled(requested);
        }
        else
        {
            _settingsService.Settings.Search.SearchHotkeyEnabled = requested;
            _settingsService.SaveDebounced();
        }

        RefreshSearchHotkeyStepControls();
    }

    private void Step3ChangeSearchHotkey_Click(object sender, RoutedEventArgs e) =>
        OpenSettingsSection("SearchSettings");

    private void SynchronizeDoubleClickToggle()
    {
        _isSynchronizingDoubleClick = true;
        try
        {
            Step3DoubleClickToggle.IsOn = _settingsService.Settings.Core.DesktopDoubleClickEnabled;
        }
        finally
        {
            _isSynchronizingDoubleClick = false;
        }
    }

    /// <summary>
    /// Same path as the settings page: the live hook service starts/stops
    /// the listener and persists; a refused start leaves the switch off.
    /// </summary>
    private void Step3DoubleClickToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isSynchronizingDoubleClick)
        {
            return;
        }

        bool requested = Step3DoubleClickToggle.IsOn;
        bool applied;
        if (App.Current.DesktopDoubleClickActivationService is { } service)
        {
            applied = service.TrySetEnabled(requested, out int errorCode);
            if (!applied)
            {
                App.Log($"[Onboarding] Desktop double-click toggle refused enabled={requested} error={errorCode}");
            }
        }
        else
        {
            _settingsService.Settings.Core.DesktopDoubleClickEnabled = requested;
            _settingsService.SaveDebounced();
            applied = true;
        }

        if (!applied)
        {
            SynchronizeDoubleClickToggle();
        }
    }

    private void Step3ChangeHotkey_Click(object sender, RoutedEventArgs e) =>
        OpenSettingsSection("InteractionWindowSettings");

    private static void OpenSettingsSection(string sectionTag) =>
        App.Current.ShowSettings(sectionTag);

    private void RefreshSummonStatus()
    {
        if (Step3StatusText is null)
        {
            return;
        }

        bool hasVisible = App.Current.HasVisibleWidgetsForOnboarding;
        string key;
        string glyph;
        if (_hasCompletedSummonPractice)
        {
            key = "Onboarding.Step3.StatusDone";
            glyph = StatusCompleteGlyph;
        }
        else if (_hasToggledDuringSummonStep)
        {
            key = hasVisible
                ? "Onboarding.Step3.StatusShown"
                : "Onboarding.Step3.StatusHidden";
            glyph = hasVisible ? StatusVisibleGlyph : StatusHiddenGlyph;
        }
        else
        {
            key = "Onboarding.Step3.StatusReady";
            glyph = StatusInfoGlyph;
        }

        Step3StatusIcon.Glyph = glyph;
        Step3StatusText.Text = _localizationService.T(key);
    }

    private async void Step3Try_Click(object sender, RoutedEventArgs e)
    {
        Step3TryButton.IsEnabled = false;
        try
        {
            await App.Current.ToggleWidgetsForOnboardingAsync();
        }
        finally
        {
            Step3TryButton.IsEnabled = true;
        }
    }

    private void OnOnboardingWidgetsVisibilityChanged(bool hasVisibleWidgets)
    {
        if (_isClosed)
        {
            return;
        }

        DispatcherQueue.TryEnqueue(() =>
        {
            if (_isClosed || _stepIndex != 2)
            {
                return;
            }

            _hasToggledDuringSummonStep = true;
            _summonToggleCount++;
            if (_summonToggleCount >= 2)
            {
                _hasCompletedSummonPractice = true;
            }

            RefreshSummonStatus();

            // The stage stops demonstrating and mirrors the real desktop:
            // the mini widgets follow the actual state, the keycap answers.
            MirrorSummonScene(hasVisibleWidgets);
        });
    }

    // ════════════════════════════════════════════════════════════
    //  Step 4: the tray menu, opened for real, plus the settings
    //  people reach for first (current value + deep link)
    // ════════════════════════════════════════════════════════════

    private void Step4OpenMenu_Click(object sender, RoutedEventArgs e)
    {
        App.Current.ShowTrayContextMenuForOnboarding();
    }

    private void SetupTrayStep()
    {
        Step4CapsuleValue.Text = _settingsService.Settings.WidgetShell.WidgetCollapseBehavior switch
        {
            "Click" => _localizationService.T("Onboarding.Quick.Collapse.Click"),
            "Smart" => _localizationService.T("Onboarding.Quick.Collapse.Smart"),
            _ => _localizationService.T("Onboarding.Quick.Collapse.Expanded"),
        };
        string effectiveMaterial = WindowsCompatibilityService.ResolveWidgetMaterialType(
            _settingsService.Settings.WidgetShell.WidgetMaterialType);
        Step4AppearanceValue.Text = effectiveMaterial switch
        {
            "MicaAlt" => _localizationService.T("Onboarding.Quick.Material.MicaAlt"),
            "Acrylic" => _localizationService.T("Onboarding.Quick.Material.Acrylic"),
            "AcrylicBase" => _localizationService.T("Onboarding.Quick.Material.AcrylicBase"),
            "Solid" => _localizationService.T("Onboarding.Quick.Material.Solid"),
            _ => _localizationService.T("Onboarding.Quick.Material.Mica"),
        };
        Step4AutoStartValue.Text = _settingsService.Settings.Core.AutoStart
            ? _localizationService.T("Common.On")
            : _localizationService.T("Common.Off");
    }

    private void Step4QuickSetting_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string sectionTag } &&
            !string.IsNullOrWhiteSpace(sectionTag))
        {
            OpenSettingsSection(sectionTag);
        }
    }

    private void Step4Organize_Click(object sender, RoutedEventArgs e)
    {
        _showDesktopOrganization?.Invoke();
    }

    // ════════════════════════════════════════════════════════════
    //  Step 5: optional feature widgets (persist immediately); the
    //  stage tiles mirror every switch
    // ════════════════════════════════════════════════════════════

    private void SetupFeatureStep()
    {
        if (_hasInitializedFeatureToggles)
        {
            return;
        }

        SynchronizeFeatureTogglesFromSettings();
    }

    private bool IsFeatureEnabled(WidgetKind kind) =>
        FeatureWidgetSettings.IsEnabled(_settingsService.Settings, kind);

    private void PlayFeatureStepEntrance()
    {
        foreach (WidgetKind kind in SceneFeatureKinds)
        {
            _featureTileStates[kind] = IsFeatureEnabled(kind);
        }

        PlayFeatureTilesEntrance(kind => _featureTileStates[kind]);
    }

    /// <summary>
    /// Brings the stage tiles in line with the persisted feature switches.
    /// Animated passes only touch tiles whose state actually changed, so a
    /// settings echo never replays a pop the user already saw.
    /// </summary>
    private void ApplyAllFeatureTileStates(bool animate)
    {
        foreach (WidgetKind kind in SceneFeatureKinds)
        {
            bool enabled = IsFeatureEnabled(kind);
            if (animate &&
                _featureTileStates.TryGetValue(kind, out bool previous) &&
                previous == enabled)
            {
                continue;
            }

            _featureTileStates[kind] = enabled;
            ApplyFeatureTileState(kind, enabled, animate);
        }
    }

    private void SetFeatureTogglesEnabled(bool enabled)
    {
        Step5TodoToggle.IsEnabled = enabled;
        Step5QuickCaptureToggle.IsEnabled = enabled;
        Step5ClipboardToggle.IsEnabled = enabled;
        Step5WeatherToggle.IsEnabled = enabled;
        Step5MusicToggle.IsEnabled = enabled;
        Step5GlanceToggle.IsEnabled = enabled;
    }

    private void Step5FeatureToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_hasInitializedFeatureToggles ||
            _isSynchronizingFeatureToggles ||
            sender is not ToggleSwitch { Tag: string kindName } toggle ||
            !Enum.TryParse(kindName, ignoreCase: false, out WidgetKind kind) ||
            !FeatureWidgetSettings.IsFeatureWidget(kind))
        {
            return;
        }

        if (_stepIndex == 4)
        {
            _featureTileStates[kind] = toggle.IsOn;
            ApplyFeatureTileState(kind, toggle.IsOn, animate: true);
        }

        _featureWidgetSelectionUpdateTask = PersistFeatureWidgetSelectionAfterAsync(
            _featureWidgetSelectionUpdateTask,
            kind,
            toggle.IsOn);
    }

    private async Task PersistFeatureWidgetSelectionAfterAsync(
        Task previousUpdate,
        WidgetKind kind,
        bool enabled)
    {
        try
        {
            await previousUpdate;

            if (App.Current.WidgetManager is { } widgetManager)
            {
                await widgetManager.SetFeatureWidgetEnabledAsync(
                    kind,
                    enabled,
                    reveal: enabled);
                return;
            }

            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
            await _settingsService.SaveAsync();
        }
        catch (Exception ex)
        {
            App.Log($"[Onboarding] Failed to persist feature selection kind={kind} enabled={enabled}: {ex}");
            FeatureWidgetSettings.SetEnabled(_settingsService.Settings, kind, enabled);
            await _settingsService.SaveAsync();
        }
    }

    private void OnFeatureWidgetSettingsChanged()
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(OnFeatureWidgetSettingsChanged);
            return;
        }

        if (_isClosed || !_hasLoaded)
        {
            return;
        }

        if (_hasInitializedFeatureToggles)
        {
            SynchronizeFeatureTogglesFromSettings();
        }

        // Keep the inline controls honest when the same values change in
        // Settings (or another process) while the guide is open.
        if (!_isChangingStoragePath)
        {
            RefreshDropStepPathAndDrives();
        }

        bool copy = IsCurrentDropModeCopy();
        if (copy != _dropSceneShowsCopy)
        {
            SetDropSceneMode(copy, replay: true);
        }

        SynchronizeDoubleClickToggle();
        RefreshSearchHotkeyStepControls();
        if (_stepIndex == 3)
        {
            SetupTrayStep();
        }

        if (_stepIndex == 4)
        {
            ApplyAllFeatureTileStates(animate: true);
        }
    }

    private void SynchronizeFeatureTogglesFromSettings()
    {
        _isSynchronizingFeatureToggles = true;
        try
        {
            Step5TodoToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Todo);
            Step5QuickCaptureToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.QuickCapture);
            Step5ClipboardToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Clipboard);
            Step5WeatherToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Weather);
            Step5MusicToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Music);
            Step5GlanceToggle.IsOn = FeatureWidgetSettings.IsEnabled(
                _settingsService.Settings,
                WidgetKind.Glance);
            _hasInitializedFeatureToggles = true;
        }
        finally
        {
            _isSynchronizingFeatureToggles = false;
        }
    }
}
