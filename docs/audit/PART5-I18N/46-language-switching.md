# Language Switching Implementation Guide

## 🎯 审计目标

提供完整的运行时语言切换实现方案，让用户可以动态更改应用程序显示语言而无需重启。

---

## 🔍 Current State Analysis

### Existing Capabilities: **None** ❌

**Detected Pattern**:
```csharp
// App.xaml.cs - NO language switching support
protected override void OnLaunched(LaunchActivatedEventArgs e)
{
    // No culture detection or switching logic
    Window window = new MainWindow();
    window.Show();
}
```

**Impact**:
- Application always runs in system default language
- No way to manually switch to preferred language
- Requires full restart to change any text
- Blocks international market expansion

---

## 🛠️ Implementation Strategy

### Phase 1: Culture Detection and Default Selection

```csharp
public class CultureManager : IDisposable
{
    private static CultureManager _instance;
    private CultureInfo _currentCulture;
    private readonly UserPreferences _preferences;
    
    private CultureManager()
    {
        _preferences = UserPreferences.Current;
        
        // Priority order for culture selection:
        // 1. User's saved preference
        // 2. System UI language
        // 3. Fallback to English (or app default)
        
        var userPreferenceName = _preferences.PreferredCulture ?? "";
        
        if (!string.IsNullOrWhiteSpace(userPreferenceName))
        {
            try
            {
                _currentCulture = new CultureInfo(userPreferenceName);
                return;
            }
            catch (CultureNotFoundException)
            {
                Logging.Warn($"User preference '{userPreferenceName}' is invalid, using system language");
            }
        }
        
        // Fall back to system language
        _currentCulture = Thread.CurrentThread.CurrentUICulture;
    }
    
    public static CultureManager Instance => 
        _instance ??= new CultureManager();
    
    public CultureInfo CurrentCulture => _currentCulture;
    
    public void ApplyCulture(CultureInfo cultureInfo, bool persist = true)
    {
        _currentCulture = cultureInfo;
        Thread.CurrentThread.CurrentCulture = cultureInfo;
        Thread.CurrentThread.CurrentUICulture = cultureInfo;
        
        // Persist user choice
        if (persist)
        {
            _preferences.PreferredCulture = cultureInfo.Name;
        }
        
        // Reload all resources with new culture
        Resources.ReloadWithCulture(cultureInfo);
        
        Logging.Info($"Language changed to: {cultureInfo.DisplayName}");
        
        // Notify UI components of change
        OnCultureChanged(cultureInfo);
    }
    
    public void ToggleToNextSupportedLanguage()
    {
        var supportedLanguages = GetSupportedCultures().ToList();
        var currentIndex = supportedLanguages.IndexOf(_currentCulture);
        
        var nextIndex = (currentIndex + 1) % supportedLanguages.Count;
        var nextCulture = supportedLanguages[nextIndex];
        
        ApplyCulture(nextCulture);
    }
    
    public IEnumerable<CultureInfo> GetSupportedCultures()
    {
        yield return new CultureInfo("zh-CN");   // Simplified Chinese (default)
        yield return new CultureInfo("en-US");   // English (fallback)
        yield return new CultureInfo("ja-JP");   // Japanese (future)
        yield return new CultureInfo("de-DE");   // German (future)
    }
    
    public event EventHandler<CultureInfo>? CultureChanged;
    
    protected virtual void OnCultureChanged(CultureInfo culture)
    {
        CultureChanged?.Invoke(this, culture);
    }
    
    public void Dispose()
    {
        // Cleanup resources if needed
    }
}
```

---

### Phase 2: Dynamic Resource Reloading

```csharp
public static class GlobalResourceReloader
{
    private static ResourceManager? _currentResourceManager;
    
    public static void ReloadWithCulture(CultureInfo newCulture)
    {
        var assembly = Assembly.GetExecutingAssembly();
        
        // Create new resource manager instances
        var mainResources = new ResourceManager("DeskBox.Resources.Strings", assembly);
        var validationResources = new ResourceManager("DeskBox.Resources.ValidationMessages", assembly);
        var tooltipResources = new ResourceManager("DeskBox.Resources.ToolTips", assembly);
        
        // Update application-wide resources dictionary
        var appResources = Application.Current.Resources;
        
        // Clear and reload based on new culture
        foreach (var resourceSet in GetResourceSets(newCulture))
        {
            foreach (DictionaryEntry entry in resourceSet)
            {
                appResources[entry.Key.ToString()] = entry.Value;
            }
        }
        
        // Also update dynamic controls that were set at runtime
        RefreshDynamicControls(newCulture);
        
        _currentResourceManager = mainResources;
    }
    
    private static IEnumerable<ResourceSet> GetResourceSets(CultureInfo culture)
    {
        yield return new ResourceSet("DeskBox.Resources.Strings", culture);
        yield return new ResourceSet("DeskBox.Resources.ValidationMessages", culture);
        yield return new ResourceSet("DeskBox.Resources.ToolTips", culture);
    }
    
    private static void RefreshDynamicControls(CultureInfo culture)
    {
        // Find all TextBlock controls and update their Text properties
        foreach (var window in Application.Current.Windows.Cast<Window>())
        {
            foreach (var control in FindAllVisualChildren<TextBlock>(window))
            {
                // If this TextBlock was created dynamically, refresh its content
                if (control.Tag != null && control.Tag.ToString().StartsWith("localized_"))
                {
                    var key = control.Tag.ToString().Replace("localized_", "");
                    control.Text = GetLocalizedString(key, culture);
                }
            }
        }
    }
    
    private static IEnumerable<T> FindAllVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            
            if (child is T typedChild)
            {
                yield return typedChild;
            }
            
            foreach (var grandChild in FindAllVisualChildren<T>(child))
            {
                yield return grandChild;
            }
        }
    }
    
    private static string GetLocalizedString(string key, CultureInfo culture)
    {
        var rm = new ResourceManager("DeskBox.Resources.Strings", Assembly.GetExecutingAssembly());
        return rm.GetString(key, culture);
    }
}
```

---

### Phase 3: UI Language Switcher Component

**XAML Component** (`Resources/Controls/LanguageSelectorView.xaml`):

```xml
<UserControl x:Class="DeskBox.Controls.LanguageSelectorView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    
    <StackPanel Orientation="Horizontal" HorizontalAlignment="Center">
        
        <!-- Label -->
        <TextBlock Text="语言:" VerticalAlignment="Center" Margin="0,0,8,0"/>
        
        <!-- Dropdown list -->
        <ComboBox x:Name="LanguageComboBox" 
                  SelectedItem="{Binding SelectedCulture, Mode=TwoWay}"
                  Width="200"
                  Height="32">
            
            <ComboBox.ItemTemplate>
                <DataTemplate>
                    <TextBlock Text="{Binding DisplayName}">
                        <TextBlock.ToolTip>
                            <ToolTip Content="{Binding NativeName}"/>
                        </TextBlock.ToolTip>
                    </TextBlock>
                </DataTemplate>
            </ComboBox.ItemTemplate>
            
        </ComboBox>
        
        <!-- Quick toggle button (cycle through languages) -->
        <Button Content="🔄" ToolTip="切换语言"
                Padding="8,4"
                Width="40" Height="32"
                Margin="12,0,0,0"
                Click="OnQuickToggleClick"/>
    </StackPanel>
</UserControl>
```

**Code-Behind**:

```csharp
public partial class LanguageSelectorView : UserControl
{
    private CultureInfo? _selectedCulture;
    
    public LanguageSelectorView()
    {
        InitializeComponent();
        
        // Initialize with current culture
        _selectedCulture = CultureManager.Instance.CurrentCulture;
        
        // Load supported languages into dropdown
        var supportedCultures = CultureManager.Instance.GetSupportedCultures()
            .Select(c => new SupportedLanguage { Culture = c })
            .ToList();
        
        LanguageComboBox.ItemsSource = supportedCultures;
        LanguageComboBox.SelectedItem = supportedCultures
            .FirstOrDefault(sl => sl.Culture.Equals(_selectedCulture));
        
        // Subscribe to selection changes
        LanguageComboBox.SelectionChanged += OnSelectionChanged;
    }
    
    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LanguageComboBox.SelectedItem is SupportedLanguage selected)
        {
            SetLanguage(selected.Culture);
        }
    }
    
    private void OnQuickToggleClick(object sender, RoutedEventArgs e)
    {
        CultureManager.Instance.ToggleToNextSupportedLanguage();
    }
    
    private void SetLanguage(CultureInfo culture)
    {
        CultureManager.Instance.ApplyCulture(culture);
        _selectedCulture = culture;
    }
}

public class SupportedLanguage
{
    public CultureInfo Culture { get; set; } = null!;
    public string DisplayName => Culture.DisplayName;
    public string NativeName => Culture.NativeDisplayName;
}
```

---

## 💡 Best Practices Summary

### ✅ DO

- Provide both manual selector AND quick-toggle shortcut
- Persist user choice immediately upon selection
- Show native names alongside translated display names (e.g., "日本語")
- Test all UI elements after language switch
- Consider RTL layout flipping for Arabic/Hebrew

### ❌ DON'T

- Force users to restart app to change language
- Assume one-size-fits-all for all cultures
- Forget to handle date/time/currency formatting per locale
- Ignore right-to-left text direction requirements

---

<div align="center">

**"Language switching isn't a feature—it's fundamental inclusivity."**

*Generated: July 22, 2026*  
*Version: 1.0*

</div>
