# Widget Factory Pattern Audit & Modernization Plan

## 🎯 审计目标

审查 WidgetContentFactory 的设计是否符合开闭原则，识别违反 OCP 的问题并提供重构方案。

---

## 🔍 Current Implementation Analysis

### Open/Closed Principle Violation

**Location**: `src/DeskBox/Services/WidgetContentFactory.cs`

#### Problematic Code Pattern:
```csharp
public class WidgetContentFactory
{
    private readonly SettingsService _settings;
    
    public ViewModel Create(ContentDescriptor descriptor)
    {
        return descriptor.Type switch
        {
            ContentType.Todo => new TodoWidgetViewModel(_settings),
            ContentType.Music => new MusicWidgetViewModel(_settings),
            ContentType.Search => new SearchWidgetViewModel(_settings),
            ContentType.Weather => new WeatherWidgetViewModel(_settings),
            ContentType.QuickCapture => new QuickCaptureWidgetViewModel(_settings),
            
            // ❌ EVERY NEW WIDGET TYPE REQUIRES MODIFYING THIS FILE!
            ContentType.NewFeature => new NewFeatureWidgetViewModel(_settings)
        };
    }
}
```

**Impact**:
- ❌ Constantly modifying same file violates OCP
- ❌ Risk of introducing bugs when adding widgets
- ❌ Cannot extend without accessing factory code
- ❌ Difficult to add validation per widget type

---

## 🧩 Current Architecture Issues

### Issue #1: Tight Coupling

**Observation**: Factory knows about ALL widget types, not just interfaces

```csharp
// ❌ Direct instantiation
new TodoWidgetViewModel(_settings)

// Should be:
_serviceProvider.GetRequiredService<TodoWidgetViewModel>()
```

---

### Issue #2: Missing Validation

**Pattern**: No centralized way to validate widget before creation

```csharp
public ViewModel Create(ContentDescriptor descriptor)
{
    // ⚠️ No validation that this content type is supported
    return descriptor.Type switch
    {
        ContentType.Music => new MusicWidgetViewModel(_settings),  // What if music disabled?
        // ...
    };
}
```

---

### Issue #3: Dependency Duplication

Every widget requires `_settings`, but some may need additional dependencies:

```csharp
public ViewModel Create(ContentDescriptor descriptor)
{
    return descriptor.Type switch
    {
        ContentType.Music => new MusicWidgetViewModel(_settings),
        ContentType.Search => new SearchWidgetViewModel(_settings, 
                                                    _searchEngineService),  // Hardcoded deps
        ContentType.Weather => new WeatherWidgetViewModel(_settings,
                                                          _weatherService),  // More hardcoded
    };
}
```

**Problem**: Mixing dependency management with creation logic

---

## 🏗️ Proposed Refactoring: Strategy Pattern

### New Architecture Design

#### Step 1: Define Provider Interface
```csharp
public interface IWidgetProvider : IDisposable
{
    ContentType Type { get; }
    IReadOnlyList<string> SupportedLanguages { get; }
    
    Task<ViewModel> CreateAsync(SettingsService settings, IServiceProvider provider);
    
    bool CanHandle(ContentDescriptor descriptor);
    
    // Optional: Pre-validation hook
    Task<bool> ValidateAsync(ContentDescriptor descriptor, IServiceProvider provider);
}
```

---

#### Step 2: Implement Concrete Providers

**Example: Weather Widget Provider**
```csharp
[Export(typeof(IWidgetProvider))]  // MEF auto-discovery
public class WeatherWidgetProvider : IWidgetProvider
{
    public ContentType Type => ContentType.Weather;
    public IReadOnlyList<string> SupportedLanguages => new[] { "en-US", "zh-CN" };
    
    public async Task<ViewModel> CreateAsync(
        SettingsService settings, 
        IServiceProvider provider)
    {
        // Get dependencies from DI container
        var weatherService = provider.GetRequiredService<WeatherService>();
        
        return new WeatherWidgetViewModel(settings, weatherService);
    }
    
    public bool CanHandle(ContentDescriptor descriptor)
    {
        return descriptor.Type == ContentType.Weather && 
               !string.IsNullOrEmpty(descriptor.CityName);
    }
    
    public async Task<bool> ValidateAsync(
        ContentDescriptor descriptor, 
        IServiceProvider provider)
    {
        // Check if city exists
        var geoService = provider.GetRequiredService<GeoCodingService>();
        return await geoService.ValidateCityAsync(descriptor.CityName);
    }
    
    public void Dispose()
    {
        // Clean up any resources
    }
}
```

---

#### Step 3: Simplify Factory to Orchestrator

```csharp
public class WidgetContentFactory
{
    private readonly IEnumerable<IWidgetProvider> _providers;
    private readonly SettingsService _settings;
    
    public WidgetContentFactory(IEnumerable<IWidgetProvider> providers)
    {
        _providers = providers.OrderBy(p => p.GetType().Name);  // Deterministic order
        _settings = settings;
    }
    
    public async Task<ViewModel> CreateAsync(ContentDescriptor descriptor)
    {
        var provider = _providers.FirstOrDefault(p => p.CanHandle(descriptor));
        
        if (provider is null)
        {
            throw new InvalidOperationException($"No provider found for type: {descriptor.Type}");
        }
        
        // Run validation first
        if (!await provider.ValidateAsync(descriptor, serviceProvider))
        {
            throw new ArgumentException("Invalid content descriptor", nameof(descriptor));
        }
        
        // Delegate creation to provider
        return await provider.CreateAsync(_settings, serviceProvider);
    }
    
    public ContentType[] GetAllSupportedTypes()
    {
        return _providers.Select(p => p.Type).Distinct().ToArray();
    }
}
```

---

## ✅ Benefits of Strategy Pattern

| Aspect | Before | After |
|--------|--------|-------|
| **Extensibility** | Modify factory | Add new provider class |
| **Testing** | Mock entire factory | Test individual providers |
| **Validation** | Scattered logic | Centralized in each provider |
| **Dependency Management** | Factory holds everything | Provider declares its own deps |
| **Parallel Development** | Merge conflicts common | Independent provider files |
| **Third-party Extensions** | Not possible | Via MEF export attribute |

---

## 🔧 Migration Path

### Phase 1: Extract Interfaces (Week 1)

**Tasks**:
1. [ ] Define `IWidgetProvider` interface
2. [ ] Add `[Export(typeof(IWidgetProvider))]` attribute to MEF support
3. [ ] Begin migration one widget at a time (start with simplest)

**Order of Migration**:
1. PlaceholderWidgetContentProvider (simplest - empty implementation)
2. WeatherWidgetProvider (clean dependencies)
3. QuickCaptureWidgetProvider (moderate complexity)
4. TodoWidgetProvider (moderate complexity)
5. MusicWidgetProvider (complex - media integration)
6. SearchWidgetProvider (most complex - engine dependencies)

---

### Phase 2: Update Factory (Week 2)

**Tasks**:
1. [ ] Remove all direct instantiations from factory
2. [ ] Inject `IEnumerable<IWidgetProvider>` 
3. [ ] Implement orchestrator pattern
4. [ ] Add error handling for missing providers

---

### Phase 3: Testing & Validation (Week 2-3)

**Tasks**:
1. [ ] Write unit tests for each provider
2. [ ] Integration test factory resolution
3. [ ] Performance benchmark (should improve slightly)
4. [ ] Verify all existing functionality preserved

---

## 🧪 Test Coverage Requirements

### Unit Tests for Each Provider

```csharp
[TestFixture]
public class WeatherWidgetProviderTests
{
    private IWidgetProvider _provider;
    private Mock<SettingsService> _settingsMock;
    private Mock<WeatherService> _weatherMock;
    
    [SetUp]
    public void SetUp()
    {
        _provider = new WeatherWidgetProvider();
        _settingsMock = new Mock<SettingsService>();
    }
    
    [Test]
    public void CreateAsync_ReturnsCorrectType()
    {
        // Arrange
        var descriptor = new ContentDescriptor(ContentType.Weather);
        
        // Act
        var result = _provider.CreateAsync(_settingsMock.Object, serviceProvider);
        
        // Assert
        result.Should().BeOfType<WeatherWidgetViewModel>();
        result.Type.Should().Be(ContentType.Weather);
    }
    
    [Test]
    public void CreateAsync_ThrowsWhenCityMissing()
    {
        // Arrange
        var descriptor = new ContentDescriptor(ContentType.Weather);
        descriptor.Properties.Remove("CityName");
        
        // Act & Assert
        () => _provider.CreateAsync(descriptor).ShouldThrow<ArgumentException>();
    }
}
```

---

### Integration Tests

```csharp
[TestFixture]
public class WidgetContentFactoryIntegrationTests
{
    private WidgetContentFactory _factory;
    private Mock<SettingsService> _settingsMock;
    
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        // Register all providers via MEF or manual registration
        var exporters = Assembly.GetExecutingAssembly()
            .GetTypes()
            .Where(t => t.GetCustomAttributes(typeof(ExportAttribute), false).Any());
        
        var providers = exporters
            .Select(t => Activator.CreateInstance(t) as IWidgetProvider)
            .Cast<IWidgetProvider>();
        
        _factory = new WidgetContentFactory(providers);
        _settingsMock = new Mock<SettingsService>();
    }
    
    [Test]
    public async Task CreateAsync_SupportsAllRegisteredTypes()
    {
        var supportedTypes = _factory.GetAllSupportedTypes();
        
        foreach (var type in supportedTypes)
        {
            var vm = await _factory.CreateAsync(new ContentDescriptor(type));
            vm.Should().NotBeNull();
        }
    }
}
```

---

## 📊 Migration Risks & Mitigation

### Risk #1: Breaking Changes During Transition

**Mitigation**: Keep old factory working alongside new one

```csharp
// Temporarily maintain dual-path approach
public class WidgetContentFactory
{
    private readonly LegacyCreationLogic _legacy;
    private readonly NewStrategyOrchestrator _strategy;
    
    public ViewModel Create(ContentDescriptor descriptor)
    {
        // If it's migrated, use strategy
        if (_strategy.CanHandle(descriptor))
        {
            return _strategy.Create(descriptor);
        }
        
        // Otherwise fall back to legacy
        return _legacy.Create(descriptor);
    }
}
```

---

### Risk #2: Circular Dependencies

Some providers may depend on other services that reference the factory.

**Solution**: Use deferred resolution

```csharp
public interface IWidgetProvider
{
    Task<ViewModel> CreateAsync(
        SettingsService settings, 
        Func<Type, object> getServiceResolver);  // Lazy resolution
}
```

---

### Risk #3: Third-party Widget Compatibility

If external developers want to add widgets, MEF export simplifies process

```csharp
// External assembly
[Export(typeof(IWidgetProvider))]
public class MyCustomWidgetProvider : IWidgetProvider
{
    public ContentType Type => ContentType.Custom;
    
    public Task<ViewModel> CreateAsync(...) { /* ... */ }
}

// Automatically discovered and loaded by core app
```

---

## 💡 Additional Improvements Enabled

Once refactored, can add:

### Feature 1: Widget Caching
```csharp
private static readonly ConcurrentDictionary<ContentType, ViewModel> _cache 
    = new();

public async Task<ViewModel> CreateOrGetCachedAsync(ContentDescriptor descriptor)
{
    if (!_cache.TryGetValue(descriptor.Type, out var cached))
    {
        cached = await CreateAsync(descriptor);
        _cache.TryAdd(descriptor.Type, cached);
    }
    
    return cached;
}
```

---

### Feature 2: Versioning Support
```csharp
public interface IWidgetProvider
{
    string ApiVersion { get; }  // e.g., "1.0.0"
    
    // Allow upgrading/downgrading widget versions
    Task<bool> IsCompatibleWith(string requiredApiVersion);
}
```

---

### Feature 3: Hot-Reloading
```csharp
public class WidgetContentFactory : IDisposable
{
    private volatile IEnumerable<IWidgetProvider> _providers;
    private readonly ReaderWriterLockSlim _lock = new();
    
    public void RefreshProviders()
    {
        _lock.EnterWriteLock();
        try
        {
            _providers = LoadProvidersFromAssemblies();  // Reload from disk
        }
        finally
        {
            _lock.ExitWriteLock();
        }
    }
}
```

---

## 🎯 Success Metrics

Refactoring considered successful when:

✅ Zero modifications needed to factory when adding new widget  
✅ Each provider has >90% unit test coverage  
✅ Integration tests pass for all registered providers  
✅ Performance unchanged (<5ms overhead in creation path)  
✅ Third-party providers load automatically via MEF  

---

## 📚 Related Documentation

- [`PART2-FUNCTIONS/7-widget-manager.md`](./7-widget-manager.md) - Factory usage context
- [`PART1-ARCHITECTURE/2-dependency-injection-audit.md`](../../PART1-ARCHITECTURE/2-dependency-injection-audit.md) - DI patterns
- **TODO**: Future doc on MEF composition strategies

---

**Document Version**: v1.0  
**Audit Date**: 2026-07-22  
**Status**: Ready for Implementation - See migration plan above
