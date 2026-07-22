# Error Handling & Exception Safety Review

## 🎯 审计目标

评估 DeskBox 的错误处理机制是否健全，识别异常捕获缺失、错误恢复不当等问题。

---

## 📊 Current Error Handling State

### Coverage Analysis

#### Well-Handled Scenarios (✅)
- UI Event handlers typically wrapped in try-catch
- Async operations use proper error propagation
- Critical system paths have fallback mechanisms

#### Poorly Handled Scenarios (❌)
- Background thread exceptions often unhandled
- Composition animation exceptions may crash render loop
- File I/O errors rarely caught and recovered gracefully

---

## 🔍 Missing Exception Guards

### Critical Gap #1: CompositionTarget Rendering Loop

**Location**: `WidgetTrayAnimationController.cs:L427`

**Current Implementation**:
```csharp
private void OnRenderingFrame(object sender, object e)
{
    // ⚠️ NO TRY-CATCH - Exception here crashes entire render loop!
    
    if (!_isRendering || _renderGeneration != Generation)
    {
        StopRendering();
        return;
    }

    var stopwatch = _renderStopwatch;
    if (stopwatch is null)
    {
        StopRendering();
        return;
    }

    // Render calculations...
    double rawProgress = Math.Clamp(stopwatch.Elapsed.TotalMilliseconds / _renderDurationMs, 0.0, 1.0);
    
    // ❌ If this throws, CompositionTarget stops calling OnRenderingFrame
    ApplyWindowOffset(...);
}
```

**Consequences**:
1. First exception → Render loop silently stops
2. No visibility into what failed
3. Widget becomes stuck/frozen
4. User must restart application to recover

**Required Fix**:
```csharp
private void OnRenderingFrame(object sender, object e)
{
    try 
    {
        if (!_isRendering || _renderGeneration != Generation)
        {
            StopRendering();
            return;
        }

        var stopwatch = _renderStopwatch;
        if (stopwatch is null)
        {
            StopRendering();
            return;
        }

        double rawProgress = Math.Clamp(
            stopwatch.Elapsed.TotalMilliseconds / _renderDurationMs, 
            0.0, 1.0
        );
        
        double easedProgress = WidgetAnimationSettings.Ease(
            rawProgress, 
            _renderEasingIntensity, 
            _renderIsShowing
        );

        ApplyWindowOffset(...);

        if (rawProgress >= 1.0)
        {
            StopRendering();
            CompleteAnimation(...);
        }
    }
    catch (Exception ex)
    {
        // Log with full context
        App.LogVerbose($"[Animation] Frame render exception: {ex.GetType().Name}: {ex.Message}");
        App.LogVerbose($"[Animation] Stack trace: {ex.StackTrace}");
        
        // Ensure cleanup even on failure
        StopRendering();
        
        // Optionally notify user for critical issues
        if (ex is OutOfMemoryException || ex is AccessViolationException)
        {
            // Crash recovery needed
            throw; // Let it bubble up for process-level handling
        }
    }
}
```

**Priority**: 🔴 CRITICAL - Must fix before release  
**Effort Estimate**: 1 hour

---

### Critical Gap #2: Settings Service Event Handlers

**Pattern Detected**:
```csharp
// In WidgetViewModel or similar
public WidgetViewModel(SettingsService settings)
{
    settings.SettingsChanged += OnSettingsChanged;  // ❌ No error handling inside handler
}

private void OnSettingsChanged(object sender, SettingsChangedEventArgs e)
{
    // What if this method throws?
    // → Exception propagates up, affects all subsequent changes
    UpdateLayout();  // Can throw if layout invalid
    RefreshData();   // Can throw if data source gone
}
```

**Impact**: Single bad setting update can break all widget instances

**Recommended Pattern**:
```csharp
private void OnSettingsChanged(object sender, SettingsChangedEventArgs e)
{
    try
    {
        UpdateLayout();
        RefreshData();
    }
    catch (Exception ex) when (ex is InvalidOperationException or NullReferenceException)
    {
        // Recoverable - log and continue
        _logger.LogWarning($"[Settings] Recovery from update error: {ex.Message}");
        
        // Reset to safe defaults
        ResetToDefaultConfiguration();
    }
    catch (Exception ex)
    {
        // Unrecoverable - report but don't crash
        _logger.LogError($"[Settings] Critical update failure: {ex}");
        
        // Notify user via non-blocking channel
        ShowErrorNotification("Unable to apply settings changes");
    }
}
```

---

### Critical Gap #3: Async Operation Errors Not Tracked

**Common Anti-Pattern**:
```csharp
// Fire-and-forget async without error tracking
async void LoadWidgetContentAsync()
{
    await LoadExternalDataAsync();  // ❌ Exception swallowed
}
```

**Problem**:
- `async void` methods cannot be awaited
- Exceptions thrown are sent to SynchronizationContext
- Often silently ignored in WinUI apps

**Better Approaches**:

#### Option 1: Return Task instead of void
```csharp
// ✅ Good - Caller can await and handle
public async Task LoadWidgetContentAsync()
{
    await LoadExternalDataAsync();
}

// Usage
try
{
    await ViewModel.LoadWidgetContentAsync();
}
catch (Exception ex)
{
    HandleLoadFailure(ex);
}
```

#### Option 2: Task.Run with completion tracking
```csharp
// For fire-and-forget scenarios where you still want error visibility
private async Task FireAndForgetSafe(Func<Task> operation)
{
    try
    {
        await operation();
    }
    catch (Exception ex)
    {
        _logger.LogError($"[FireAndForget] Error in background task: {ex}");
    }
}
```

---

## 📋 Error Handling Checklist

### Pre-Release Validation

| Area | Check Status | Notes |
|------|--------------|-------|
| All event handlers wrapped in try-catch | ⏳ Pending | 50+ locations to verify |
| Async void replaced with async Task | ⏳ Pending | Search for pattern |
| File I/O errors handled gracefully | ⏳ Pending | Common failure point |
| Network request timeouts configured | ⏳ Pending | Weather/external APIs |
| UI thread exceptions logged properly | ⏳ Pending | Debug vs Release mode |
| Critical errors trigger user notification | ⏳ Pending | Non-blocking feedback |
| Application state preserved after recoverable errors | ⏳ Pending | Undo/crash recovery |

---

## 🛡️ Best Practices Template

### Universal Error Handler Structure

```csharp
private async Task ExecuteWithRecoveryAsync(
    Func<Task> operation,
    string operationName,
    Action<Exception>? onError = null)
{
    try
    {
        await operation();
    }
    catch (OperationCanceledException)
    {
        // Expected cancellation - log as info
        _logger.LogDebug($"[Recovery] Operation {operationName} cancelled");
    }
    catch (TaskCanceledException)
    {
        // Timeout scenario
        _logger.LogWarning($"[Recovery] Operation {operationName} timed out");
        
        // Trigger fallback mechanism
        FallbackLogic();
    }
    catch (HttpRequestException httpEx) when (httpEx.StatusCode == HttpStatusCode.NotFound)
    {
        // Resource not found - common, expected case
        _logger.LogInformation($"[Recovery] Resource not found: {httpEx.Message}");
    }
    catch (Exception ex)
    {
        // Unexpected exception - log everything
        _logger.LogError(ex, $"[Recovery] Unhandled error in {operationName}: {ex.Message}");
        
        // Persist error for later analysis
        await SaveErrorReportAsync(ex, operationName);
        
        // Notify caller/user if appropriate
        onError?.Invoke(ex);
        
        // Attempt graceful degradation
        TryDeactivateFeature(operationName);
    }
}
```

---

## 🔧 Known Issues Requiring Fixes

### Issue #EH001: Animation Frame Exception Silence

**File**: `src/DeskBox/Services/WidgetTrayAnimationController.cs`  
**Line**: ~427  
**Severity**: 🔴 Critical  
**Fix Required**: Add try-catch wrapper to OnRenderingFrame  

**Evidence**: Code inspection shows no exception handling around render logic

---

### Issue #EH002: Search Engine Errors Ignored

**File**: `src/DeskBox/Services/SearchEngineService.cs`  
**Patterns Found**:
```csharp
// Multiple search operations without error handling
var results = SearchIndexedFiles(query);  // If index corrupts → crash
rankResults(results);                     // Algorithm exception possible
```

**Risk**: Full-text search failures can take down app

**Fix Priority**: 🟠 High  
**Effort**: 2-3 hours

---

### Issue #EH003: File Watcher Crashes

**File**: `FolderWatcherService.cs`  
**Concern**: FileSystemWatcher events not protected

```csharp
private void OnFileChanged(object sender, FileSystemEventArgs e)
{
    // ❌ What if file deleted between event and read?
    var content = File.ReadAllText(e.FullPath);
}
```

**Fix**: Wrap in try-catch, implement retry logic

---

## 🧪 Testing Strategy

### Unit Test Scenarios

1. **Exception Injection Tests**
```csharp
[Test]
public async Task OnSettingsChanged_WhenThrows_ExceptionCapturedAndRecovered()
{
    // Arrange
    var vm = CreateTestViewModel();
    _mockSettings.Setup(x => x.RefreshData).Throws<InvalidOperationException>();
    
    // Act
    await vm.OnSettingsChanged(null, EventArgs.Empty);
    
    // Assert
    vm.IsHealthy.Should().BeTrue();  // Still functional after error
    _mockLogger.Verify(v => v.LogWarning(It.IsAny<string>()));
}
```

2. **Graceful Degradation Tests**
```csharp
[Test]
public async Task LoadWidgetContent_WhenNetworkFallsBackToLocalCache()
{
    // Simulate network failure
    _httpClientMock.SetupAllMethods().Throws<HttpRequestException>();
    
    // Verify fallback triggered
    await vm.RefreshData();
    
    vm.DataSource.Should().Be(LocalCache);  // Not empty!
}
```

---

### Integration Tests

3. **Long-Running Stress Tests**
```csharp
[Test]
public async Task ContinuousOperations_Over1Hour_NoUnhandledExceptions()
{
    using var stopwatch = Stopwatch.StartNew();
    
    while (stopwatch.Elapsed < TimeSpan.FromHours(1))
    {
        // Perform various operations
        await vm.CreateWidgetAsync();
        await vm.UpdateWidgetAsync();
        await vm.DeleteWidgetAsync();
        
        await Task.Delay(1000);
    }
    
    // Final assertion: no uncaught exceptions during run
    _exceptionCollector.Count.Should().Be(0);
}
```

---

## 📝 Monitoring Recommendations

### Runtime Logging Levels

| Severity | When To Use | Action Required |
|----------|-------------|-----------------|
| Trace | Verbose debugging | Never in production |
| Debug | Development troubleshooting | Optional in beta builds |
| Info | Normal operations | Yes - key milestones |
| Warning | Recoverable errors | Always - alert monitoring |
| Error | Unrecoverable but contained | Always - immediate investigation |
| Critical | Process-threatening issues | Emergency page/on-call |

### Structured Logging Format

```json
{
  "timestamp": "2026-07-22T10:30:15Z",
  "level": "ERROR",
  "component": "WidgetManager",
  "operation": "CreateWidget",
  "message": "Failed to create widget instance",
  "exception_type": "InvalidOperationException",
  "exception_message": "Widget type 'Music' not registered",
  "stack_trace": "...",
  "context": {
    "user_id": "abc123",
    "widget_count": 15,
    "available_types": ["Todo", "Weather"]
  }
}
```

---

## 🔄 Error Recovery Patterns

### Pattern 1: Circuit Breaker

For external service calls:
```csharp
private readonly CircuitBreaker _weatherApiCircuit = new(
    failureThreshold: 5,
    resetTimeout: TimeSpan.FromMinutes(5)
);

public async Task<WeatherData> GetWeatherAsync(string city)
{
    if (_weatherApiCircuit.IsClosed())
    {
        return DefaultWeatherData;  // Fallback
    }
    
    try
    {
        var data = await _api.GetWeather(city);
        _weatherApiCircuit.RecordSuccess();
        return data;
    }
    catch
    {
        _weatherApiCircuit.RecordFailure();
        throw;
    }
}
```

---

### Pattern 2: Retry With Backoff

```csharp
public async Task<bool> SaveWidgetStateAsync()
{
    var attempts = 0;
    const int maxAttempts = 3;
    
    while (attempts < maxAttempts)
    {
        try
        {
            await _storage.Save(State);
            return true;
        }
        catch (IOException) when (attempts < maxAttempts - 1)
        {
            attempts++;
            await Task.Delay(TimeSpan.FromSeconds(attempts) * 2);  // Exponential backoff
        }
    }
    
    return false;  // Give up after max attempts
}
```

---

## 🎯 Success Criteria

Error handling considered adequate when:

✅ All public entry points have try-catch coverage  
✅ Async operations propagate errors correctly  
✅ User-facing errors provide actionable messages  
✅ System errors are logged with sufficient context  
✅ Automatic recovery attempts implemented  
✅ Manual intervention only required for catastrophic failures  

Current deskbox status: ⚠️ **Incomplete** - See critical gaps above

---

## 📚 Related Documentation

- [`PART1-ARCHITECTURE/4-threading-model.md`](./4-threading-model.md) - Exception safety in render loops
- [`PART1-ARCHITECTURE/5-memory-leak-analysis.md`](./5-memory-leak-analysis.md) - Resource cleanup on errors
- [`PART3-PERFORMANCE/31-shutdown-graceful.md`](../../PART3-PERFORMANCE/31-shutdown-graceful.md) - Exit error handling

---

**Document Version**: v1.0  
**Audit Date**: 2026-07-22  
**Status**: Urgent Action Required - Animation frame exception handling priority #1
