using System.Globalization;
using DeskBox.Controls;
using DeskBox.Models;
using DeskBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Windows.Storage;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace DeskBox.Views.SettingsSections;

public sealed partial class GlanceWidgetSettingsSection : UserControl
{
    private static readonly string[] DisplayOptions = ["Time", "Date", "Year", "Weekday", "Calendar"];
    private sealed partial class SelectionItem : ComboBoxItem
    {
        public SelectionItem(string label, object value)
        {
            Content = label;
            Value = value;
        }

        public object Value { get; }
    }
    private GlanceWidgetStore? _store;
    private readonly GlanceImageService _imageService = new();
    private readonly GlanceTraditionalCalendarService _traditionalCalendarService = new();
    private readonly SystemFontCatalogService _fontCatalogService = new();
    private readonly DispatcherTimer _scaleSaveTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly DispatcherTimer _imageTransparencySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly DispatcherTimer _calendarTransparencySaveTimer = new() { Interval = TimeSpan.FromMilliseconds(280) };
    private readonly SemaphoreSlim _instanceSelectionGate = new(1, 1);
    private GlanceWidgetData _settings = new();
    private string? _selectedWidgetId;
    private IntPtr _ownerWindow;
    private bool _isLoading;
    private bool _isSectionLoaded;
    private bool _instanceRefreshQueued;
    private long _instanceSelectionVersion;
    private string _instanceStateSignature = string.Empty;
    private string _optionsLocalizationSignature = string.Empty;

    public GlanceWidgetSettingsSection()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _scaleSaveTimer.Tick += ScaleSaveTimer_Tick;
        _imageTransparencySaveTimer.Tick += ImageTransparencySaveTimer_Tick;
        _calendarTransparencySaveTimer.Tick += CalendarTransparencySaveTimer_Tick;
        WidgetDangerActionStyle.Apply(DeleteInstanceMenuItem);
    }

    private LocalizationService Localization => App.Current.LocalizationService;

    public void SetOwnerWindow(IntPtr ownerWindow) => _ownerWindow = ownerWindow;

    public void SelectWidget(string widgetId)
    {
        if (!string.IsNullOrWhiteSpace(widgetId))
        {
            _selectedWidgetId = widgetId;
        }
    }

    public async Task RefreshFromStoreAsync()
    {
        await RefreshInstancesAsync(_selectedWidgetId);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _isSectionLoaded = true;
        App.Current.SettingsService.SettingsChanged -= OnAppSettingsChanged;
        App.Current.SettingsService.SettingsChanged += OnAppSettingsChanged;
        await RefreshFromStoreAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _isSectionLoaded = false;
        App.Current.SettingsService.SettingsChanged -= OnAppSettingsChanged;
    }

    private void OnAppSettingsChanged()
    {
        if (!_isSectionLoaded ||
            _instanceRefreshQueued)
        {
            return;
        }

        _instanceRefreshQueued = true;
        if (!DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    if (!_isSectionLoaded ||
                        App.Current.WidgetManager is not { } manager)
                    {
                        return;
                    }

                    IReadOnlyList<GlanceWidgetInstanceInfo> instances =
                        manager.GetGlanceWidgetInstances();
                    if (!string.Equals(
                            _instanceStateSignature,
                            CreateInstanceStateSignature(manager, instances),
                            StringComparison.Ordinal))
                    {
                        await RefreshInstancesAsync(_selectedWidgetId);
                    }
                }
                catch (Exception ex)
                {
                    App.Log($"[GlanceSettings] Instance state refresh failed: {ex}");
                }
                finally
                {
                    _instanceRefreshQueued = false;
                }
            }))
        {
            _instanceRefreshQueued = false;
        }
    }

    private async Task RefreshInstancesAsync(string? preferredWidgetId)
    {
        await _instanceSelectionGate.WaitAsync();
        try
        {
            await RefreshInstancesCoreAsync(preferredWidgetId);
        }
        finally
        {
            _instanceSelectionGate.Release();
        }
    }

    private async Task RefreshInstancesCoreAsync(string? preferredWidgetId)
    {
        UpdateInstanceLocalization();
        if (App.Current.WidgetManager is not { } manager)
        {
            ApplyEmptyInstanceState();
            return;
        }

        IReadOnlyList<GlanceWidgetInstanceInfo> instances = manager.GetGlanceWidgetInstances();
        _instanceStateSignature = CreateInstanceStateSignature(manager, instances);
        GlanceWidgetInstanceInfo? selected = instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, preferredWidgetId, StringComparison.Ordinal)) ??
            instances.FirstOrDefault();

        _isLoading = true;
        try
        {
            _selectedWidgetId = selected?.Id;
            InstanceComboBox.SelectedItem = SynchronizeInstanceOptions(
                instances,
                selected?.Id);
            ApplyInstanceManagerState(manager, instances, selected);
            EnsureOptionsPopulated();

            if (selected is null)
            {
                _store = null;
                _settings = new GlanceWidgetData();
                return;
            }

            _store = GlanceWidgetStore.ForWidget(selected!.Id);
            _settings = await _store.LoadAsync();
            ApplySettingsToControls();
            UpdateCacheSize();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private static string CreateInstanceStateSignature(
        WidgetManager manager,
        IEnumerable<GlanceWidgetInstanceInfo> instances)
    {
        return $"{manager.IsGlanceFeatureEnabled}:" + string.Join(
            "|",
            instances.Select(instance =>
                $"{instance.Id.Length}:{instance.Id}" +
                $"{instance.Name.Length}:{instance.Name}" +
                $"{instance.IsEnabled}"));
    }

    private void ApplyEmptyInstanceState()
    {
        _isLoading = true;
        try
        {
            _selectedWidgetId = null;
            _instanceStateSignature = string.Empty;
            _store = null;
            _settings = new GlanceWidgetData();
            InstanceComboBox.Items.Clear();
            InstanceSettingsPanel.Visibility = Visibility.Collapsed;
            InstanceEnabledToggle.IsOn = false;
            InstanceEnabledToggle.IsEnabled = false;
            InstanceMoreButton.IsEnabled = false;
            LocateInstanceMenuItem.IsEnabled = false;
            DuplicateInstanceMenuItem.IsEnabled = false;
            RenameInstanceMenuItem.IsEnabled = false;
            DeleteInstanceMenuItem.IsEnabled = false;
            InstanceManagerCard.Description = Localization.Format(
                "Glance.Instances.Description",
                0);
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void UpdateInstanceLocalization()
    {
        InstanceComboBox.PlaceholderText = Localization.T("Glance.Instances.Empty");
        LocateInstanceMenuItem.Text = Localization.T("Glance.Instances.Locate");
        DuplicateInstanceMenuItem.Text = Localization.T("Glance.Instances.Duplicate");
        RenameInstanceMenuItem.Text = Localization.T("Common.Rename");
        DeleteInstanceMenuItem.Text = Localization.T("Common.Delete");
    }

    private void PopulateOptions()
    {
        SetOptions(
            LayoutComboBox,
            (Localization.T("Glance.Layout.Immersive"), GlanceLayoutMode.Immersive),
            (Localization.T("Glance.Layout.Centered"), GlanceLayoutMode.Centered),
            (Localization.T("Glance.Layout.Editorial"), GlanceLayoutMode.Editorial),
            (Localization.T("Glance.Layout.Calendar"), GlanceLayoutMode.Calendar));
        SetOptions(
            BackgroundSourceComboBox,
            (Localization.T("Glance.Background.Files"), GlanceBackgroundSource.LocalFiles),
            (Localization.T("Glance.Background.Folder"), GlanceBackgroundSource.LocalFolder),
            (Localization.T("Glance.Background.Bing"), GlanceBackgroundSource.Bing),
            (Localization.T("Glance.Background.Online"), GlanceBackgroundSource.Online));
        SetOptions(
            OnlineImageCategoryComboBox,
            (Localization.T("Glance.Background.Category.Featured"), GlanceOnlineImageCategory.Featured),
            (Localization.T("Glance.Background.Category.Landscapes"), GlanceOnlineImageCategory.Landscapes),
            (Localization.T("Glance.Background.Category.Cities"), GlanceOnlineImageCategory.Cities),
            (Localization.T("Glance.Background.Category.Architecture"), GlanceOnlineImageCategory.Architecture),
            (Localization.T("Glance.Background.Category.Animals"), GlanceOnlineImageCategory.Animals),
            (Localization.T("Glance.Background.Category.Plants"), GlanceOnlineImageCategory.Plants),
            (Localization.T("Glance.Background.Category.Astronomy"), GlanceOnlineImageCategory.Astronomy),
            (Localization.T("Glance.Background.Category.People"), GlanceOnlineImageCategory.People));
        SetOptions(
            RotationComboBox,
            (Localization.T("Glance.Rotation.Manual"), 0d),
            (Localization.T("Glance.Rotation.10Seconds"), 10d / 60d),
            (Localization.T("Glance.Rotation.30Seconds"), 30d / 60d),
            (Localization.T("Glance.Rotation.60Seconds"), 1d),
            (Localization.T("Glance.Rotation.2Minutes"), 2d),
            (Localization.T("Glance.Rotation.5Minutes"), 5d),
            (Localization.T("Glance.Rotation.10Minutes"), 10d),
            (Localization.T("Glance.Rotation.30Minutes"), 30d),
            (Localization.T("Glance.Rotation.1Hour"), 60d),
            (Localization.T("Glance.Rotation.6Hours"), 360d),
            (Localization.T("Glance.Rotation.Daily"), 1440d));
        SetOptions(
            TransitionComboBox,
            (Localization.T("Glance.Transition.None"), GlanceTransitionMode.None),
            (Localization.T("Glance.Transition.CrossFade"), GlanceTransitionMode.CrossFade),
            (Localization.T("Glance.Transition.SlideFade"), GlanceTransitionMode.SlideFade),
            (Localization.T("Glance.Transition.ZoomFade"), GlanceTransitionMode.ZoomFade));
        SetOptions(
            SpeedComboBox,
            (Localization.T("Glance.Speed.Fast"), GlanceTransitionSpeed.Fast),
            (Localization.T("Glance.Speed.Standard"), GlanceTransitionSpeed.Standard),
            (Localization.T("Glance.Speed.Relaxed"), GlanceTransitionSpeed.Relaxed));
        SetOptions(
            ReadabilityComboBox,
            (Localization.T("Glance.Readability.None"), GlanceReadabilityMode.None),
            (Localization.T("Glance.Readability.Soft"), GlanceReadabilityMode.Soft),
            (Localization.T("Glance.Readability.Strong"), GlanceReadabilityMode.Strong));
        SetOptions(
            CalendarMaterialComboBox,
            (Localization.T("Glance.CalendarMaterial.FollowSystem"), GlanceCalendarMaterialMode.FollowSystem),
            (Localization.T("Glance.CalendarMaterial.FollowImage"), GlanceCalendarMaterialMode.FollowImage));
        GlanceTraditionalCalendarMode resolvedTraditionalCalendar =
            _traditionalCalendarService.ResolveMode(
                GlanceTraditionalCalendarMode.Auto,
                Localization.CurrentCultureName);
        SetOptions(
            TraditionalCalendarComboBox,
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.None), GlanceTraditionalCalendarMode.None),
            (
                string.Format(
                    CultureInfo.CurrentCulture,
                    Localization.T("Glance.TraditionalCalendar.Auto"),
                    GetTraditionalCalendarLabel(resolvedTraditionalCalendar)),
                GlanceTraditionalCalendarMode.Auto),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.ChineseLunar), GlanceTraditionalCalendarMode.ChineseLunar),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.UmAlQura), GlanceTraditionalCalendarMode.UmAlQura),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Hijri), GlanceTraditionalCalendarMode.Hijri),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.IndianSaka), GlanceTraditionalCalendarMode.IndianSaka),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.JapaneseEra), GlanceTraditionalCalendarMode.JapaneseEra),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Bangla), GlanceTraditionalCalendarMode.Bangla),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Julian), GlanceTraditionalCalendarMode.Julian),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Hebrew), GlanceTraditionalCalendarMode.Hebrew),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.Persian), GlanceTraditionalCalendarMode.Persian),
            (GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode.ThaiBuddhist), GlanceTraditionalCalendarMode.ThaiBuddhist));

        (string Label, object Value)[] fonts =
        [
            (Localization.T("Glance.Typography.SystemDefault"), string.Empty),
            .. _fontCatalogService.GetFontFamilies().Select(name => (name, (object)name))
        ];
        SetOptions(FontComboBox, fonts);
        SetOptions(
            TimeFormatComboBox,
            (Localization.T("Glance.Typography.FollowSystem"), GlanceTimeFormatMode.FollowSystem),
            (Localization.T("Glance.Typography.Hour24"), GlanceTimeFormatMode.Hour24),
            (Localization.T("Glance.Typography.Hour12"), GlanceTimeFormatMode.Hour12));
    }

    private void EnsureOptionsPopulated()
    {
        string localizationSignature =
            $"{Localization.CurrentCultureName}|{CultureInfo.CurrentCulture.Name}";
        bool optionsArePopulated =
            LayoutComboBox.Items.Count > 0 &&
            BackgroundSourceComboBox.Items.Count > 0 &&
            OnlineImageCategoryComboBox.Items.Count > 0 &&
            RotationComboBox.Items.Count > 0 &&
            TransitionComboBox.Items.Count > 0 &&
            SpeedComboBox.Items.Count > 0 &&
            ReadabilityComboBox.Items.Count > 0 &&
            CalendarMaterialComboBox.Items.Count > 0 &&
            TraditionalCalendarComboBox.Items.Count > 0 &&
            FontComboBox.Items.Count > 0 &&
            TimeFormatComboBox.Items.Count > 0;
        if (optionsArePopulated &&
            string.Equals(
                _optionsLocalizationSignature,
                localizationSignature,
                StringComparison.Ordinal))
        {
            return;
        }

        PopulateOptions();
        _optionsLocalizationSignature = localizationSignature;
    }

    private static void SetOptions(
        ComboBox comboBox,
        params (string Label, object Value)[] options)
    {
        comboBox.Items.Clear();
        foreach ((string label, object value) in options)
        {
            comboBox.Items.Add(new SelectionItem(label, value));
        }
    }

    private async Task LoadSelectedInstanceAsync(
        string selectedWidgetId,
        long selectionVersion)
    {
        if (App.Current.WidgetManager is not { } manager)
        {
            await RefreshInstancesCoreAsync(null);
            return;
        }

        IReadOnlyList<GlanceWidgetInstanceInfo> instances = manager.GetGlanceWidgetInstances();
        GlanceWidgetInstanceInfo? selected = instances.FirstOrDefault(instance =>
            string.Equals(instance.Id, selectedWidgetId, StringComparison.Ordinal));
        if (selected is null)
        {
            await RefreshInstancesCoreAsync(null);
            return;
        }

        GlanceWidgetStore store = GlanceWidgetStore.ForWidget(selected.Id);
        GlanceWidgetData settings = await store.LoadAsync();
        if (selectionVersion != _instanceSelectionVersion ||
            InstanceComboBox.SelectedItem is not SelectionItem { Value: string currentWidgetId } ||
            !string.Equals(currentWidgetId, selectedWidgetId, StringComparison.Ordinal))
        {
            return;
        }

        _isLoading = true;
        try
        {
            _instanceStateSignature = CreateInstanceStateSignature(manager, instances);
            _selectedWidgetId = selected.Id;
            ApplyInstanceManagerState(manager, instances, selected);
            EnsureOptionsPopulated();
            _store = store;
            _settings = settings;
            ApplySettingsToControls();
            UpdateCacheSize();
        }
        finally
        {
            _isLoading = false;
        }
    }

    private void ApplyInstanceManagerState(
        WidgetManager manager,
        IReadOnlyCollection<GlanceWidgetInstanceInfo> instances,
        GlanceWidgetInstanceInfo? selected)
    {
        bool hasSelection = selected is not null;
        InstanceSettingsPanel.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        InstanceMoreButton.IsEnabled = hasSelection;
        LocateInstanceMenuItem.IsEnabled = selected?.IsEnabled == true;
        DuplicateInstanceMenuItem.IsEnabled = hasSelection;
        RenameInstanceMenuItem.IsEnabled = hasSelection;
        DeleteInstanceMenuItem.IsEnabled = hasSelection;
        InstanceEnabledToggle.IsEnabled = hasSelection && manager.IsGlanceFeatureEnabled;
        InstanceEnabledToggle.IsOn = selected?.IsEnabled == true;
        InstanceManagerCard.Description = Localization.Format(
            "Glance.Instances.Description",
            instances.Count);
        if (!manager.IsGlanceFeatureEnabled)
        {
            InstanceManagerCard.Description =
                $"{InstanceManagerCard.Description} {Localization.T("Glance.Instances.MasterOff")}";
        }
    }

    private SelectionItem? SynchronizeInstanceOptions(
        IReadOnlyList<GlanceWidgetInstanceInfo> instances,
        string? selectedWidgetId)
    {
        SelectionItem[] options = InstanceComboBox.Items
            .OfType<SelectionItem>()
            .ToArray();
        bool optionsMatch = options.Length == instances.Count &&
            options.Select((option, index) =>
                    option.Value is string id &&
                    string.Equals(id, instances[index].Id, StringComparison.Ordinal) &&
                    string.Equals(option.Content as string, instances[index].Name, StringComparison.Ordinal))
                .All(matches => matches);
        if (!optionsMatch)
        {
            InstanceComboBox.Items.Clear();
            foreach (GlanceWidgetInstanceInfo instance in instances)
            {
                InstanceComboBox.Items.Add(new SelectionItem(instance.Name, instance.Id));
            }

            options = InstanceComboBox.Items
                .OfType<SelectionItem>()
                .ToArray();
        }

        return options.FirstOrDefault(option =>
            option.Value is string id &&
            string.Equals(id, selectedWidgetId, StringComparison.Ordinal));
    }

    private void ApplySettingsToControls()
    {
        bool wasLoading = _isLoading;
        _isLoading = true;
        try
        {
            SelectOption(LayoutComboBox, _settings.Layout);
            SelectOption(BackgroundSourceComboBox, _settings.BackgroundSource);
            SelectOption(OnlineImageCategoryComboBox, _settings.OnlineImageCategory);
            SelectOption(RotationComboBox, _settings.RotationIntervalMinutes);
            SelectOption(TransitionComboBox, _settings.Transition);
            SelectOption(SpeedComboBox, _settings.TransitionSpeed);
            SelectOption(ReadabilityComboBox, _settings.Readability);
            SelectOption(CalendarMaterialComboBox, _settings.CalendarMaterialMode);
            SelectOption(TraditionalCalendarComboBox, _settings.TraditionalCalendarMode);
            SelectOption(FontComboBox, _settings.TimeFontFamily ?? string.Empty);
            SelectOption(TimeFormatComboBox, _settings.TimeFormat);
            RandomOrderToggle.IsOn = _settings.RandomOrder;
            TimeScaleSlider.Value = _settings.TimeScale;
            BackgroundImageTransparencySlider.Value = _settings.BackgroundImageTransparency;
            CalendarImageTransparencySlider.Value = _settings.CalendarImageMaterialTransparency;
            ShowChineseFestivalsToggle.IsOn = _settings.ShowChineseFestivals;
            ShowPhotoControlsToggle.IsOn = _settings.ShowPhotoControls;
            UpdateDisplaySelectionSummary();
            UpdateLocalSourceState();
            UpdateBackgroundImageTransparencyState();
            UpdateCalendarMaterialState();
        }
        finally
        {
            _isLoading = wasLoading;
        }
    }

    private static void SelectOption(ComboBox comboBox, object value)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<SelectionItem>()
            .FirstOrDefault(option => Equals(option.Value, value));
    }

    private async Task SaveAsync(Action<GlanceWidgetData> update)
    {
        GlanceWidgetStore? store = _store;
        if (_isLoading || store is null)
        {
            return;
        }

        update(_settings);
        await store.SaveAsync(_settings);
    }

    private async Task FlushPendingSavesAsync()
    {
        bool hasPendingSave = _scaleSaveTimer.IsEnabled ||
            _imageTransparencySaveTimer.IsEnabled ||
            _calendarTransparencySaveTimer.IsEnabled;
        _scaleSaveTimer.Stop();
        _imageTransparencySaveTimer.Stop();
        _calendarTransparencySaveTimer.Stop();
        if (hasPendingSave && _store is { } store)
        {
            await store.SaveAsync(_settings);
        }
    }

    private async void InstanceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InstanceComboBox.SelectedItem is not SelectionItem { Value: string selectedWidgetId })
        {
            return;
        }

        long selectionVersion = ++_instanceSelectionVersion;
        if (string.Equals(_selectedWidgetId, selectedWidgetId, StringComparison.Ordinal))
        {
            return;
        }

        await _instanceSelectionGate.WaitAsync();
        try
        {
            if (selectionVersion != _instanceSelectionVersion)
            {
                return;
            }

            await FlushPendingSavesAsync();
            if (selectionVersion != _instanceSelectionVersion)
            {
                return;
            }

            await LoadSelectedInstanceAsync(selectedWidgetId, selectionVersion);
        }
        catch (Exception ex)
        {
            App.Log($"[GlanceSettings] Instance selection failed: {ex}");
        }
        finally
        {
            _instanceSelectionGate.Release();
        }
    }

    private async void InstanceEnabledToggle_Toggled(object sender, RoutedEventArgs e)
    {
        if (_isLoading ||
            string.IsNullOrWhiteSpace(_selectedWidgetId) ||
            App.Current.WidgetManager is not { } manager)
        {
            return;
        }

        await manager.SetGlanceWidgetInstanceEnabledAsync(
            _selectedWidgetId,
            InstanceEnabledToggle.IsOn);
        await RefreshInstancesAsync(_selectedWidgetId);
    }

    private async void AddInstanceButton_Click(object sender, RoutedEventArgs e)
    {
        if (App.Current.WidgetManager is not { } manager)
        {
            return;
        }

        await FlushPendingSavesAsync();
        GlanceWidgetInstanceInfo created = await manager.CreateGlanceWidgetAsync();
        await RefreshInstancesAsync(created.Id);
    }

    private async void LocateInstanceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_selectedWidgetId) &&
            App.Current.WidgetManager is { } manager)
        {
            await manager.LocateGlanceWidgetAsync(_selectedWidgetId);
        }
    }

    private async void DuplicateInstanceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedWidgetId) ||
            App.Current.WidgetManager is not { } manager)
        {
            return;
        }

        await FlushPendingSavesAsync();
        GlanceWidgetInstanceInfo created = await manager.CreateGlanceWidgetAsync(_selectedWidgetId);
        await RefreshInstancesAsync(created.Id);
    }

    private async void RenameInstanceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedWidgetId) ||
            App.Current.WidgetManager is not { } manager ||
            XamlRoot is null)
        {
            return;
        }

        GlanceWidgetInstanceInfo? selected = manager.GetGlanceWidgetInstances()
            .FirstOrDefault(instance =>
                string.Equals(instance.Id, _selectedWidgetId, StringComparison.Ordinal));
        if (selected is null)
        {
            return;
        }

        var nameBox = new TextBox
        {
            Text = selected.Name,
            MinWidth = 320,
            MaxLength = 80,
            PlaceholderText = Localization.T("Glance.Instances.RenamePlaceholder")
        };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.T("Glance.Instances.RenameTitle"),
            Content = nameBox,
            PrimaryButtonText = Localization.T("Common.Save"),
            CloseButtonText = Localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary ||
            string.IsNullOrWhiteSpace(nameBox.Text))
        {
            return;
        }

        await manager.RenameWidgetAsync(selected.Id, nameBox.Text);
        await RefreshInstancesAsync(selected.Id);
    }

    private async void DeleteInstanceMenuItem_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_selectedWidgetId) ||
            App.Current.WidgetManager is not { } manager ||
            XamlRoot is null)
        {
            return;
        }

        GlanceWidgetInstanceInfo? selected = manager.GetGlanceWidgetInstances()
            .FirstOrDefault(instance =>
                string.Equals(instance.Id, _selectedWidgetId, StringComparison.Ordinal));
        if (selected is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = Localization.Format("Glance.Instances.DeleteTitle", selected.Name),
            Content = new TextBlock
            {
                Text = Localization.T("Glance.Instances.DeleteDescription"),
                TextWrapping = TextWrapping.Wrap
            },
            PrimaryButtonText = Localization.T("Common.Delete"),
            CloseButtonText = Localization.T("Common.Cancel"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await FlushPendingSavesAsync();
        await manager.RemoveGlanceWidgetAsync(selected.Id);
        _selectedWidgetId = null;
        await RefreshInstancesAsync(null);
    }

    private void DisplayContentDropDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not DropDownButton button)
        {
            return;
        }

        SettingsMultiSelectMenu.Show(
            button,
            DisplayOptions,
            GetDisplayOptionLabel,
            IsDisplayOptionSelected,
            _ => true,
            ToggleDisplayOption);
    }

    private async void ToggleDisplayOption(string option)
    {
        GlanceDisplayElement? element = option switch
        {
            "Time" => GlanceDisplayElement.Time,
            "Date" => GlanceDisplayElement.Date,
            "Year" => GlanceDisplayElement.Year,
            "Weekday" => GlanceDisplayElement.Weekday,
            "Calendar" => GlanceDisplayElement.Calendar,
            _ => null
        };
        if (element is null)
        {
            return;
        }

        bool isVisible = GlanceWidgetSettingsPolicy.IsDisplayElementVisible(
            _settings,
            element.Value);
        GlanceWidgetSettingsPolicy.SetDisplayElement(
            _settings,
            element.Value,
            !isVisible);

        SynchronizeLayoutSelection();
        UpdateDisplaySelectionSummary();
        UpdateCalendarMaterialState();
        if (_store is { } store)
        {
            await store.SaveAsync(_settings);
        }
    }

    private async void LayoutComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LayoutComboBox.SelectedItem is SelectionItem { Value: GlanceLayoutMode layout })
        {
            await SaveAsync(settings =>
                GlanceWidgetSettingsPolicy.SetLayout(settings, layout));
            UpdateDisplaySelectionSummary();
            UpdateCalendarMaterialState();
        }
    }

    private void SynchronizeLayoutSelection()
    {
        bool wasLoading = _isLoading;
        _isLoading = true;
        try
        {
            SelectOption(LayoutComboBox, _settings.Layout);
        }
        finally
        {
            _isLoading = wasLoading;
        }
    }

    private async void CalendarMaterialComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CalendarMaterialComboBox.SelectedItem is SelectionItem { Value: GlanceCalendarMaterialMode mode })
        {
            await SaveAsync(settings => settings.CalendarMaterialMode = mode);
            UpdateCalendarMaterialState();
        }
    }

    private async void TraditionalCalendarComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TraditionalCalendarComboBox.SelectedItem is SelectionItem { Value: GlanceTraditionalCalendarMode mode })
        {
            await SaveAsync(settings => settings.TraditionalCalendarMode = mode);
            UpdateCalendarMaterialState();
        }
    }

    private async void ShowChineseFestivalsToggle_Toggled(object sender, RoutedEventArgs e)
        => await SaveAsync(settings => settings.ShowChineseFestivals = ShowChineseFestivalsToggle.IsOn);

    private async void BackgroundSourceComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BackgroundSourceComboBox.SelectedItem is SelectionItem { Value: GlanceBackgroundSource source })
        {
            await SaveAsync(settings => settings.BackgroundSource = source);
            UpdateLocalSourceState();
        }
    }

    private async void OnlineImageCategoryComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OnlineImageCategoryComboBox.SelectedItem is SelectionItem { Value: GlanceOnlineImageCategory category })
        {
            await SaveAsync(settings => settings.OnlineImageCategory = category);
        }
    }

    private async void RotationComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RotationComboBox.SelectedItem is SelectionItem { Value: double minutes })
        {
            await SaveAsync(settings => settings.RotationIntervalMinutes = minutes);
        }
    }

    private async void TransitionComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TransitionComboBox.SelectedItem is SelectionItem { Value: GlanceTransitionMode transition })
        {
            await SaveAsync(settings => settings.Transition = transition);
        }
    }

    private async void SpeedComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SpeedComboBox.SelectedItem is SelectionItem { Value: GlanceTransitionSpeed speed })
        {
            await SaveAsync(settings => settings.TransitionSpeed = speed);
        }
    }

    private async void ReadabilityComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReadabilityComboBox.SelectedItem is SelectionItem { Value: GlanceReadabilityMode readability })
        {
            await SaveAsync(settings => settings.Readability = readability);
        }
    }

    private void BackgroundImageTransparencySlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.BackgroundImageTransparency = Math.Clamp(e.NewValue, 0.0, 1.0);
        UpdateBackgroundImageTransparencyState();
        _imageTransparencySaveTimer.Stop();
        _imageTransparencySaveTimer.Start();
    }

    private async void ImageTransparencySaveTimer_Tick(object? sender, object e)
    {
        _imageTransparencySaveTimer.Stop();
        if (_store is { } store)
        {
            await store.SaveAsync(_settings);
        }
    }

    private void CalendarImageTransparencySlider_ValueChanged(
        object sender,
        RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.CalendarImageMaterialTransparency = Math.Clamp(e.NewValue, 0.0, 1.0);
        UpdateCalendarMaterialState();
        _calendarTransparencySaveTimer.Stop();
        _calendarTransparencySaveTimer.Start();
    }

    private async void CalendarTransparencySaveTimer_Tick(object? sender, object e)
    {
        _calendarTransparencySaveTimer.Stop();
        if (_store is { } store)
        {
            await store.SaveAsync(_settings);
        }
    }

    private async void FontComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontComboBox.SelectedItem is SelectionItem { Value: string font })
        {
            await SaveAsync(settings => settings.TimeFontFamily = string.IsNullOrWhiteSpace(font) ? null : font);
        }
    }

    private async void TimeFormatComboBox_SelectionChanged(
        object sender,
        SelectionChangedEventArgs e)
    {
        if (TimeFormatComboBox.SelectedItem is SelectionItem
            {
                Value: GlanceTimeFormatMode timeFormat
            })
        {
            await SaveAsync(settings => settings.TimeFormat = timeFormat);
        }
    }

    private async void RandomOrderToggle_Toggled(object sender, RoutedEventArgs e)
        => await SaveAsync(settings => settings.RandomOrder = RandomOrderToggle.IsOn);

    private async void ShowPhotoControlsToggle_Toggled(object sender, RoutedEventArgs e)
        => await SaveAsync(settings => settings.ShowPhotoControls = ShowPhotoControlsToggle.IsOn);

    private void TimeScaleSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_isLoading)
        {
            return;
        }

        _settings.TimeScale = e.NewValue;
        _scaleSaveTimer.Stop();
        _scaleSaveTimer.Start();
    }

    private async void ScaleSaveTimer_Tick(object? sender, object e)
    {
        _scaleSaveTimer.Stop();
        if (_store is { } store)
        {
            await store.SaveAsync(_settings);
        }
    }

    private async void ChooseFilesButton_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".webp");
        picker.FileTypeFilter.Add(".bmp");
        InitializeWithWindow.Initialize(picker, _ownerWindow);
        IReadOnlyList<StorageFile> files = await picker.PickMultipleFilesAsync();
        if (files.Count == 0)
        {
            return;
        }

        GlanceWidgetSettingsPolicy.SetLocalImageFiles(
            _settings,
            files.Select(file => file.Path));
        if (_store is not { } store)
        {
            return;
        }

        await store.SaveAsync(_settings);
        ApplySettingsToControls();
    }

    private async void ChooseFolderButton_Click(object sender, RoutedEventArgs e)
    {
        string? folder = await FolderPickerService.PickFolderAsync(_ownerWindow);
        if (string.IsNullOrWhiteSpace(folder))
        {
            return;
        }

        _settings.BackgroundSource = GlanceBackgroundSource.LocalFolder;
        _settings.LocalFolderPath = folder;
        if (_store is not { } store)
        {
            return;
        }

        await store.SaveAsync(_settings);
        ApplySettingsToControls();
    }

    private async void ClearLocalSourceButton_Click(object sender, RoutedEventArgs e)
    {
        GlanceWidgetSettingsPolicy.ClearLocalSource(_settings);

        if (_store is not { } store)
        {
            return;
        }

        await store.SaveAsync(_settings);
        ApplySettingsToControls();
    }

    private async void ClearCacheButton_Click(object sender, RoutedEventArgs e)
    {
        await _imageService.ClearCacheAsync();
        UpdateCacheSize();
    }

    private void UpdateLocalSourceState()
    {
        OnlineImageCategoryCard.Visibility = _settings.BackgroundSource == GlanceBackgroundSource.Online
            ? Visibility.Visible
            : Visibility.Collapsed;
        LocalSourceCard.Visibility = _settings.BackgroundSource is
            GlanceBackgroundSource.LocalFiles or GlanceBackgroundSource.LocalFolder
                ? Visibility.Visible
                : Visibility.Collapsed;
        ChooseFilesButton.Visibility = _settings.BackgroundSource == GlanceBackgroundSource.LocalFiles
            ? Visibility.Visible
            : Visibility.Collapsed;
        ChooseFolderButton.Visibility = _settings.BackgroundSource == GlanceBackgroundSource.LocalFolder
            ? Visibility.Visible
            : Visibility.Collapsed;
        ClearLocalSourceButton.IsEnabled = _settings.BackgroundSource switch
        {
            GlanceBackgroundSource.LocalFiles => _settings.LocalImagePaths.Count > 0,
            GlanceBackgroundSource.LocalFolder => !string.IsNullOrWhiteSpace(_settings.LocalFolderPath),
            _ => false
        };
        LocalSourceSummary.Text = _settings.BackgroundSource switch
        {
            GlanceBackgroundSource.LocalFiles when _settings.LocalImagePaths.Count > 0 => string.Format(
                CultureInfo.CurrentCulture,
                Localization.T("Glance.Background.LocalSummary"),
                _settings.LocalImagePaths.Count),
            GlanceBackgroundSource.LocalFiles => Localization.T("Glance.Status.NoLocalImages"),
            GlanceBackgroundSource.LocalFolder => _settings.LocalFolderPath ?? Localization.T("Glance.Status.NoLocalImages"),
            _ => string.Empty
        };
    }

    private void UpdateCalendarMaterialState()
    {
        bool calendarVisible = _settings.Layout == GlanceLayoutMode.Calendar && _settings.ShowCalendar;
        TraditionalCalendarCard.Visibility = calendarVisible ? Visibility.Visible : Visibility.Collapsed;
        bool chineseLunarSelected = _traditionalCalendarService.ResolveMode(
            _settings.TraditionalCalendarMode,
            Localization.CurrentCultureName) == GlanceTraditionalCalendarMode.ChineseLunar;
        ChineseFestivalCard.Visibility = calendarVisible && chineseLunarSelected
            ? Visibility.Visible
            : Visibility.Collapsed;
        CalendarMaterialCard.Visibility = calendarVisible ? Visibility.Visible : Visibility.Collapsed;
        CalendarImageTransparencyCard.Visibility = calendarVisible &&
            _settings.CalendarMaterialMode == GlanceCalendarMaterialMode.FollowImage
                ? Visibility.Visible
                : Visibility.Collapsed;
        CalendarImageTransparencyValue.Text = $"{Math.Round(_settings.CalendarImageMaterialTransparency * 100):0}%";
    }

    private void UpdateBackgroundImageTransparencyState()
    {
        BackgroundImageTransparencyValue.Text =
            $"{Math.Round(_settings.BackgroundImageTransparency * 100):0}%";
    }

    private void UpdateDisplaySelectionSummary()
    {
        var selected = new List<string>(5);
        if (_settings.ShowTime) selected.Add(Localization.T("Glance.Display.Time"));
        if (_settings.ShowDate) selected.Add(Localization.T("Glance.Display.Date"));
        if (_settings.ShowYear) selected.Add(Localization.T("Glance.Display.Year"));
        if (_settings.ShowWeekday) selected.Add(Localization.T("Glance.Display.Weekday"));
        if (_settings.ShowCalendar) selected.Add(Localization.T("Glance.Display.Calendar"));
        DisplayContentDropDownButton.Content = selected.Count == 0
            ? Localization.T("Glance.Display.PhotoOnly")
            : string.Join(Localization.T("Glance.Display.Separator"), selected);
    }

    private string GetDisplayOptionLabel(string option) => option switch
    {
        "Time" => Localization.T("Glance.Display.Time"),
        "Date" => Localization.T("Glance.Display.Date"),
        "Year" => Localization.T("Glance.Display.Year"),
        "Weekday" => Localization.T("Glance.Display.Weekday"),
        "Calendar" => Localization.T("Glance.Display.Calendar"),
        _ => option
    };

    private bool IsDisplayOptionSelected(string option) => option switch
    {
        "Time" => _settings.ShowTime,
        "Date" => _settings.ShowDate,
        "Year" => _settings.ShowYear,
        "Weekday" => _settings.ShowWeekday,
        "Calendar" => _settings.ShowCalendar,
        _ => false
    };

    private void UpdateCacheSize()
    {
        long bytes = _imageService.GetCacheSizeBytes();
        CacheSizeText.Text = bytes < 1024 * 1024
            ? $"{bytes / 1024d:0.#} KB"
            : $"{bytes / 1024d / 1024d:0.#} MB";
    }

    private string GetTraditionalCalendarLabel(GlanceTraditionalCalendarMode mode) =>
        Localization.T(mode switch
        {
            GlanceTraditionalCalendarMode.None => "Glance.TraditionalCalendar.None",
            GlanceTraditionalCalendarMode.ChineseLunar => "Glance.TraditionalCalendar.ChineseLunar",
            GlanceTraditionalCalendarMode.UmAlQura => "Glance.TraditionalCalendar.UmAlQura",
            GlanceTraditionalCalendarMode.Hijri => "Glance.TraditionalCalendar.Hijri",
            GlanceTraditionalCalendarMode.IndianSaka => "Glance.TraditionalCalendar.IndianSaka",
            GlanceTraditionalCalendarMode.JapaneseEra => "Glance.TraditionalCalendar.JapaneseEra",
            GlanceTraditionalCalendarMode.Bangla => "Glance.TraditionalCalendar.Bangla",
            GlanceTraditionalCalendarMode.Julian => "Glance.TraditionalCalendar.Julian",
            GlanceTraditionalCalendarMode.Hebrew => "Glance.TraditionalCalendar.Hebrew",
            GlanceTraditionalCalendarMode.Persian => "Glance.TraditionalCalendar.Persian",
            GlanceTraditionalCalendarMode.ThaiBuddhist => "Glance.TraditionalCalendar.ThaiBuddhist",
            _ => "Glance.TraditionalCalendar.None"
        });
}
