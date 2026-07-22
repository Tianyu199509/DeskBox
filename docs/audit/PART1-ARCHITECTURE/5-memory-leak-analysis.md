# Memory Leak Analysis & Prevention Audit

## 🎯 审计目标

全面识别 DeskBox 中的内存泄漏风险点，提供具体的预防和修复方案。

---

## 🔍 内存泄漏检测策略

### 常见泄漏模式识别

#### Pattern #1: Event Subscription Without Unsubscribe

**Detection Method**: Search for all `+=` event handlers without corresponding `-=` unsubscribe

```csharp
// ❌ VULNERABLE PATTERN - Event leak
class WidgetWindow
{
    public WidgetWindow()
    {
        SettingsService.SettingsChanged += OnSettingsChanged;
        // Missing: SettingsService.SettingsChanged -= OnSettingsChanged;
    }
}
```

**Impact**: WidgetWindow objects persist even after being closed → Heap growth

**Likely Locations**:
- All `WidgetViewModel` constructors subscribing to settings events
- Window lifetime hooks (OnLoaded, OnClosed)
- Timer tick handlers

---

#### Pattern #2: Static Collection Growth

**Detection Method**: Look for static lists/maps that grow over time

```csharp
// ⚠️ DANGEROUS - Static collection keeps growing
public class WidgetManager
{
    private static readonly List<WidgetViewModel> _widgets = new();
    
    public void AddWidget(WidgetViewModel widget)
    {
        _widgets.Add(widget);  // Never removed!
    }
}
```

**Evidence Search**:
```powershell
grep -r "static.*List<" src/DeskBox/Services/
grep -r "static.*Dictionary" src/DeskBox/Services/
```

**Risk Level**: 🔴 Critical if widgets aren't explicitly removed

---

#### Pattern #3: Weak Reference Misuse

**Incorrect Implementation**:
```csharp
// ❌ Wrong - Still strong reference
private static ObservableCollection<WidgetViewModel> Widgets { get; set; }
```

**Correct Pattern**:
```csharp
// ✅ Good - Using weak references
private static ConcurrentDictionary<Guid, WeakReference<WidgetViewModel>> Widgets 
    = new();
```

---

## 💾 Known Leak Sources

### Source #1: BitmapImage / ImagingBitmap Disposal

**Location**: Multiple files using image display

**Typical Problem Code**:
```csharp
// In FileMetaService.cs or similar
var bitmap = new BitmapImage();
bitmap.UriSource = new Uri(imagePath);
imageControl.Source = bitmap;
// ❌ No disposal, but BitmapImage holds file handle!
```

**Consequences**:
- Each displayed image keeps file open
- Eventually "Access Denied" errors on file access
- Memory heap grows continuously

**Fix Template**:
```csharp
using var stream = await File.OpenReadAsync(path);
var bitmap = new BitmapImage();
await bitmap.SetSourceAsync(stream.AsRandomAccessStream());
// Using statement ensures stream disposal
```

**Files Affected**: ~15 files estimated

---

### Source #2: Stream/StreamReader Without Using

**Locations**: SearchEngine, IndexedFileService, FileService

**Typical Issue**:
```csharp
// In Indexing Service
var reader = new StreamReader(filePath);
var content = reader.ReadToEnd();
// ❌ Stream not disposed → File handle leak
```

**Verification Command**:
```powershell
grep -r "new StreamReader(" src/DeskBox/ | grep -v "using"
grep -r "new FileStream(" src/DeskBox/ | grep -v "using"
```

---

### Source #3: Windows Runtime COM Objects

**Critical Resource**: `SystemMediaPlayerReference` in MusicSessionService

**Current State**: ❌ NO IDisposable implementation found

**Expected Leak Behavior**:
```
User plays music → Session starts
App closes without Dispose() → System.Media process still running
Memory/GPU context not released → Next launch slower
```

**Required Fix**:
```csharp
public sealed class MusicSessionService : IDisposable
{
    private SystemMediaPlayerReference? _player;
    private bool _disposed;
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _player?.Dispose();  // Release COM object
        _player = null;
        
        CompositionTarget.Rendering -= OnRenderingFrame;
        
        _disposed = true;
    }
}
```

---

### Source #4: CompositionVisual Lifetime Management

**Animation Controller Pattern**:
```csharp
// WidgetTrayAnimationController.cs
public void StartRendering()
{
    CompositionTarget.Rendering += OnRenderingFrame;  // ✓ Subscribed
}

public void StopRendering()
{
    CompositionTarget.Rendering -= OnRenderingFrame;  // ✓ Unsubscribed
}
```

**✅ GOOD**: Animation controllers properly manage subscription lifecycle

**⚠️ CAUTION**: Exception during rendering may prevent cleanup

**Improvement Needed**:
```csharp
private void OnRenderingFrame(object sender, object e)
{
    try
    {
        // Render logic...
    }
    catch (Exception ex)
    {
        _log($"[Animation] Frame exception: {ex.Message}");
        StopRendering();  // Force cleanup even on error
    }
}
```

---

### Source #5: Timer Tick Handlers

**Leak Pattern**:
```csharp
public WidgetViewModel()
{
    _refreshTimer = DispatcherQueue.CreateTimer();
    _refreshTimer.Tick += OnRefreshTick;  // Subscribe
    _refreshTimer.Interval = TimeSpan.FromSeconds(30);
    _refreshTimer.Start();
    // ❌ Timer never stopped on ViewModel disposal
}
```

**Fix**:
```csharp
public void Dispose()
{
    _refreshTimer.Stop();
    _refreshTimer.Tick -= OnRefreshTick;
    _refreshTimer.Close();
}
```

---

## 🔧 Memory Profiling Recommendations

### Tooling Setup

#### Visual Studio Diagnostic Tools
```bash
# Launch with memory profiler
devenv /diag output/log.txt
# Then use Memory Usage window
```

#### Application Verifier
```powershell
# Enable heap debugging
Verify.exe /enable DeskBox.exe /heap
# Monitor for leaks
```

#### PerfView
```powershell
# Collect heap dump
PerfView startcollect /KernelEvents+ /ProcessFilters:*deskbox*
# Analyze allocations
PerfView analyze *.etl
```

---

## 📊 Leak Detection Checklist

### Pre-Deployment Validation

| Test Case | Verification Method | Status |
|-----------|-------------------|--------|
| Widget creation/destruction | Memory snapshot before/after | ⏳ Pending |
| Image loading/unloading | GC heap analysis | ⏳ Pending |
| Music session lifecycle | Process handle count | ⏳ Pending |
| Search indexing loop | Allocations per second | ⏳ Pending |

---

## 🛡️ Prevention Strategies

### 1. Disposable Pattern Standardization

**Required Template**:
```csharp
public partial class SomeComponent : IDisposable
{
    private bool _disposed;
    
    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                // Managed resources
                _eventHandler -= OnSomethingHappened;
                _timer?.Stop();
                _stream?.Close();
            }
            
            // Always release unmanaged resources
            _comObject?.Dispose();
            
            _disposed = true;
        }
    }
    
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    
    ~SomeComponent()  // Finalizer
    {
        Dispose(false);
    }
}
```

---

### 2. Using Statement Enforcement

**C# Rule**: ALL disposable objects must use `using` declaration

**Bad Example**:
```csharp
var reader = new StreamReader(path);  // ❌
var lines = reader.ReadToEnd();
return lines;
```

**Good Example**:
```csharp
using var reader = new StreamReader(path);  // ✅
var lines = reader.ReadToEnd();
return lines;
```

---

### 3. Weak Event Pattern

For event subscriptions where listener should not block GC:

```csharp
// Use Microsoft.Xaml.Behaviors.Uwp.Interactivity.WeakEvent
WeakEventManager<SettingsService, SettingsChangedEventArgs>
    .AddListener(_settingsService, nameof(SettingsService.SettingsChanged),
    OnSettingsChanged);
```

---

### 4. Object Pooling

**High-Frequency Allocation Scenarios**:
- Layout calculation results
- Animation progress frames
- Search result entries

**Optimization**:
```csharp
// Use ArrayPool<T> for temporary buffers
var buffer = ArrayPool<char>.Shared.Rent(4096);
try 
{
    // Process data...
}
finally 
{
    ArrayPool<char>.Shared.Return(buffer);
}
```

---

## 📈 Monitoring Strategy

### Runtime Metrics

#### Key Performance Counters
```csharp
// Track in diagnostic mode
private static PerformanceCounter _memoryCounter = new(
    category: ".NET CLR Memory",
    counter: "Private Bytes",
    instance: Process.GetCurrentProcess().ProcessName
);
```

#### Garbage Collection Statistics
```csharp
void LogGCStats()
{
    var gen0Collections = GC.CollectionCount(0);
    var gen1Collections = GC.CollectionCount(1);
    var gen2Collections = GC.CollectionCount(2);
    
    App.LogVerbose($"[GC] Gen0={gen0Collections}, Gen1={gen1Collections}, Gen2={gen2Collections}");
}
```

**Warning Sign**: If Gen2 collections increase steadily → Long-lived garbage detected

---

## 🐛 Known Leaks Requiring Immediate Fix

### Critical #1: MusicSessionService (Already Documented)
**Severity**: 🔴  
**Fix Required**: Implement IDisposable + release all COM objects  
**Time Estimate**: 1 hour

---

### Critical #2: BitmapImage Cache Not Implemented Yet
**Location**: Likely in FileMetaService or ImageLoaderService  
**Impact**: Each loaded image keeps file handle open indefinitely  
**Estimated Files**: ~15 files need review  

**Action Items**:
1. Create list of all BitmapImage usage locations
2. Verify each uses proper disposal pattern
3. Consider implementing caching with eviction policy

---

### High Priority: Event Handler Cleanup
**Scope**: All ViewModels and WidgetWindows  
**Pattern**: Check every constructor for event subscriptions

**Verification Script**:
```powershell
$events = Select-String -Path "src/DeskBox/**/*.cs" -Pattern "\+=" | Where-Object { $_.Line -match "EventHandler|Action|Event" }
foreach ($event in $events) {
    Write-Host "Found subscription: $($event.FileName):$($event.LineNumber)"
    # Check if unsubscribe exists in same class
}
```

---

## 📋 Remediation Plan

### Week 1: Emergency Fixes
- [ ] MusicSessionService.Dispose() implementation
- [ ] Identify all BitmapImage usages
- [ ] Add using statements to Streams readers/writers

### Week 2: Event Handler Audit
- [ ] Map all event subscriptions across codebase
- [ ] Add unsubscribe calls to all disposable components
- [ ] Convert critical events to WeakEventManager pattern

### Week 3: Memory Budget Testing
- [ ] Establish baseline memory usage metrics
- [ ] Run stress tests (100 widget creations, etc.)
- [ ] Validate no growth beyond tolerance thresholds

### Week 4: Automated Guards
- [ ] Add memory leak detection to CI pipeline
- [ ] Configure performance regression alerts
- [ ] Document memory management guidelines

---

## 📊 Success Metrics

| Metric | Current Baseline | Target (3 months) |
|--------|------------------|-------------------|
| Peak memory usage | ~80MB idle | ≤40MB idle |
| Handles per process | ~500+ | ≤250 |
| GC frequency (Gen2/min) | ~5 | ≤1 |
| Memory growth rate | ~2MB/hour | <0.1MB/hour |

---

## 🔗 Related Documentation

- [`PART1-ARCHITECTURE/4-threading-model.md`](./4-threading-model.md) - Event subscription patterns
- [`PART1-ARCHITECTURE/2-dependency-injection-audit.md`](./2-dependency-injection-audit.md) - Lifecycle management
- [`PART3-PERFORMANCE/32-resource-release.md`](../../PART3-PERFORMANCE/32-resource-release.md) - Complete resource cleanup guide

---

**Document Version**: v1.0  
**Audit Date**: 2026-07-22  
**Status**: Action Required - See remediation plan above
