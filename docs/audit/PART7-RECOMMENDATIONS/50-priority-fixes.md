# Priority Fixes Checklist

## 📋 Executive Summary

This document provides actionable tracking for all issues identified in the DeskBox code audit, organized by priority and assigned to respective team members.

**Generated**: 2026-07-22  
**Total Issues Identified**: 52+ across all audit documents  
**Critical Items Requiring Immediate Action**: 12

---

## 🔴 P0 - Critical (Must Fix Within This Sprint)

These issues pose immediate risks to stability, data integrity, or user experience.

### Item #CRIT-001: MusicSessionService Resource Leak

**Severity**: 🔴 CRITICAL  
**Location**: `src/DeskBox/Services/MusicSessionService.cs`  
**Issue**: COM objects not released on disposal → Background process persistence  
**Impact**: Memory/GPU leaks, system resource exhaustion  

**Fix Required**:
```csharp
public sealed class MusicSessionService : IDisposable
{
    private bool _disposed;
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _mediaPlayer?.Dispose();
        CompositionTarget.Rendering -= OnRenderingFrame;
        _disposed = true;
    }
}
```

**Owner**: Senior Developer  
**Deadline**: End of Week 1  
**ETA**: 1 hour  
**Status**: ⏳ Pending

---

### Item #CRIT-002: Animation Frame Exception Handling Missing

**Severity**: 🔴 CRITICAL  
**Location**: `src/DeskBox/Services/WidgetTrayAnimationController.cs:L427`  
**Issue**: OnRenderingFrame throws exception → Render loop silently dies  
**Impact**: Widget becomes frozen, no recovery possible  

**Fix Required**: Wrap render logic in try-catch (see [`PART1-ARCHITECTURE/6-error-handling-review.md`](./PART1-ARCHITECTURE/6-error-handling-review.md))

**Owner**: Animation Team  
**Deadline**: End of Week 1  
**ETA**: 30 minutes  
**Status**: ⏳ Pending

---

### Item #CRIT-003: State Persistence Non-Atomic Write Risk

**Severity**: 🔴 CRITICAL  
**Location**: `src/DeskBox/Services/WidgetManager.Storage.cs`  
**Issue**: File.WriteAllTextAsync() can corrupt config on crash  
**Impact**: User settings lost forever  

**Fix Required**: Two-phase commit pattern
```csharp
await File.WriteAllTextAsync(path + ".tmp", json);
File.Move(path + ".tmp", path, overwrite: true);
```

**Owner**: Backend Developer  
**Deadline**: End of Week 1  
**ETA**: 2 hours  
**Status**: ⏳ Pending

---

### Item #CRIT-004: Zero i18n Infrastructure Blocks Global Release

**Severity**: 🔴 CRITICAL  
**Scope**: Entire codebase  
**Issue**: All text hardcoded → Cannot support multiple languages  
**Impact**: Market expansion impossible until fixed  

**Fix Required**: Setup .resx resource files structure (see [`PART5-I18N/44-i18n-strategy.md`](./PART5-I18N/44-i18n-strategy.md))

**Owner**: Localization Lead  
**Deadline**: End of Week 2  
**ETA**: 6 hours initial setup  
**Status**: ⏳ Pending

---

### Item #CRIT-005: BitmapImage Disposal Leaks File Handles

**Severity**: 🔴 CRITICAL  
**Scope**: ~15 files across Views and Services  
**Issue**: Images keep file handles open indefinitely  
**Impact**: Eventually "Access Denied" errors  

**Fix Required**: Add using statements to all BitmapImage usage (see detailed list in memory leak audit)

**Owner**: UI Developers (all)  
**Deadline**: End of Week 2  
**ETA**: 4 hours total  
**Status**: ⏳ Pending

---

### Item #CRIT-006: SettingsEvent Deadlock Potential

**Severity**: 🔴 CRITICAL  
**Location**: Multiple places where SettingsService events trigger widget updates  
**Issue**: Event handler might lock while waiting on same thread  
**Impact**: Main thread hangs → App unresponsive  

**Fix Required**: Convert to async event handlers with properConfigureAwait(false)

**Owner**: Core Team  
**Deadline**: End of Week 2  
**ETA**: 3 hours  
**Status**: ⏳ Pending

---

## 🟠 P1 - High (Address Within Next Sprint)

High-priority items that impact performance or user experience but are not immediately critical.

### Item #HIGH-001: GDI Handle Leak in Z-Order Operations

**Severity**: 🟠 HIGH  
**Location**: `WidgetManager.ZOrder.cs` and related Win32 APIs  
**Issue**: BringWindowToTop/BringWindowToFront calls without handle validation  
**Impact**: Handle exhaustion over time  

**Fix Required**: Validate IntPtr before Win32 calls, add exception handling

**Owner**: Platform Engineer  
**Deadline**: Week 3  
**ETA**: 2 hours  
**Status**: ⏳ Pending

---

### Item #HIGH-002: Drag Operation Performance Lag

**Severity**: 🟠 HIGH  
**Location**: `WidgetManager.TrayAnimation.cs` drag event handlers  
**Issue**: Individual animation per widget instead of batched updates  
**Impact**: Laggy dragging feel, especially on 144Hz displays  

**Fix Required**: Implement debounced layout recalculation at 60fps max

**Owner**: Graphics Programmer  
**Deadline**: Week 3  
**ETA**: 6 hours  
**Status**: ⏳ Pending

---

### Item #HIGH-003: Event Handler Cleanup Not Implemented

**Severity**: 🟠 HIGH  
**Scope**: All ViewModels subscribing to global events  
**Issue**: Events never unsubscribed when ViewModel disposed  
**Impact**: Memory growth over time  

**Fix Required**: Implement IDisposable pattern with unsubscribe calls

**Owner**: All VM Developers  
**Deadline**: Week 3  
**ETA**: 3 hours  
**Status**: ⏳ Pending

---

### Item #HIGH-004: WidgetManager Too Large (SRP Violation)

**Severity**: 🟠 HIGH  
**Location**: `WidgetManager.cs` ~1100+ LOC  
**Issue**: Eight distinct responsibilities mixed together  
**Impact**: Difficult to test/maintain  

**Fix Required**: Split into 5 focused services (see module boundaries audit)

**Owner**: System Architect  
**Deadline**: Week 4-6  
**ETA**: 30 hours over 3 weeks  
**Status**: ⏳ Pending

---

### Item #HIGH-005: SettingsViewModel Monolithic Structure

**Severity**: 🟠 HIGH  
**Location**: `SettingsViewModel.cs` ~900+ LOC  
**Issue**: Mixing UI state with feature-specific options  
**Impact**: Confusing mental model, hard to extend  

**Fix Required**: Extract partial classes for each feature area

**Owner**: Frontend Lead  
**Deadline**: Week 4  
**ETA**: 8 hours  
**Status**: ⏳ Pending

---

### Item #HIGH-006: Static ServiceRegistry Pattern Hinders Testing

**Severity**: 🟠 HIGH  
**Location**: `ServiceRegistry.cs` static methods  
**Issue**: Cannot mock dependencies for unit tests  
**Impact**: Test coverage stuck at <10%  

**Fix Required**: Introduce IServiceRegistry interface with DI injection

**Owner**: Test Automation Lead  
**Deadline**: Week 4  
**ETA**: 4 hours  
**Status**: ⏳ Pending

---

## 🟡 P2 - Medium (Optimize During Regular Development)

Important improvements that enhance maintainability but don't require immediate action.

### Item #MED-001: WidgetContentFactory OCP Violation

**Severity**: 🟡 MEDIUM  
**Location**: `WidgetContentFactory.Create()` switch statement  
**Issue**: Adding new widget type requires modifying factory code  
**Impact**: Constant merge conflicts, regression risk  

**Fix Required**: Strategy pattern migration (see dedicated doc)

**Owner**: Design Patterns Expert  
**Deadline**: Month 2  
**ETA**: 15 hours  
**Status**: ⏳ Future

---

### Item #MED-002: Floating Point Precision Errors in Layout

**Severity**: 🟡 MEDIUM  
**Location**: `WidgetCapsuleArrangementCalculator.cs`  
**Issue**: Accumulation errors during position calculations  
**Impact**: Pixel-level misalignment after many widgets  

**Fix Required**: Recalculate from base position rather than incremental updates

**Owner**: Math/Algorithm Specialist  
**Deadline**: Month 2  
**ETA**: 2 hours  
**Status**: ⏳ Future

---

### Item #MED-003: Search Engine Architecture Refactor Needed

**Severity**: 🟡 MEDIUM  
**Location**: `SearchEngineService.cs` too many concerns  
**Issue**: Indexing, searching, ranking mixed together  
**Impact**: Hard to swap out search backend  

**Fix Required**: Split into IIndexer, IQueryEngine, IRanker interfaces

**Owner**: Search Platform Team  
**Deadline**: Month 2-3  
**ETA**: 10 hours  
**Status**: ⏳ Future

---

### Item #MED-004: Missing Unit Tests Coverage

**Severity**: 🟡 MEDIUM  
**Current Coverage**: <10%  
**Target**: >80% within 6 months  

**Tasks**:
1. [ ] Setup Coverlet integration (Week 2)
2. [ ] Write first 10 unit tests (Week 2-3)
3. [ ] Achieve 40% coverage (Month 1)
4. [ ] Reach 80% coverage (Month 3)

**Owner**: QA Team  
**Deadline**: Rolling  
**ETA**: 40 hours total  
**Status**: ⏳ Ongoing

---

## 🟢 P3 - Low (Future Enhancement Opportunities)

Nice-to-have improvements for polish and best practices.

### Item #LOW-001: Documentation Comments Completion

**Current**: Minimal XML comments  
**Target**: All public APIs documented  

**ETA**: 8 hours over next quarter

---

### Item #LOW-002: Code Style Standardization

**Current**: Inconsistent naming conventions across teams  
**Target**: Unified style enforced via Roslyn analyzers  

**ETA**: 4 hours setup + 2 hours cleanup

---

### Item #LOW-003: Accessibility Features

**Current**: No ARIA labels, limited keyboard navigation  
**Target**: WCAG 2.1 Level AA compliance  

**ETA**: 40 hours over Q4 2026

---

## 📊 Progress Tracking Dashboard

| Priority | Total Items | Completed | Remaining | % Done |
|----------|-------------|-----------|-----------|--------|
| P0 Critical | 6 | 0 | 6 | 0% |
| P1 High | 6 | 0 | 6 | 0% |
| P2 Medium | 4 | 0 | 4 | 0% |
| P3 Low | 3 | 0 | 3 | 0% |
| **TOTAL** | **19** | **0** | **19** | **0%** |

*Note: Numbers shown represent major issue categories, actual count is 52+ individual items*

---

## 🗓️ Timeline Overview

### Week 1 (Immediate Emergency Response)
- ✅ Create audit docs (Done)
- 🔴 Fix MusicSessionService.Dispose()
- 🔴 Add Animation exception handling
- 🔴 Implement atomic writes
- 🔴 Begin i18n infrastructure setup

### Week 2-3 (Stabilization Sprint)
- 🔴 Complete BitmapImage fixes (~15 files)
- 🔴 Fix SettingsEvent deadlock potential
- 🟠 Resolve GDI handle leaks
- 🟠 Optimize drag operations
- 🔴 Expand i18n to all UI strings

### Week 4-6 (Architecture Modernization)
- 🟠 Split WidgetManager into focused services
- 🟠 Refactor SettingsViewModel
- 🟠 Replace static ServiceRegistry
- 🟡 Start WidgetContentFactory migration

### Month 2-3 (Quality Foundation)
- 🟡 Finish factory migration
- 🟡 Improve floating-point precision
- 🟡 Refactor search engine
- 🟡 Reach 50% unit test coverage

### Month 4-6 (Long-term Health)
- 🟢 Add comprehensive documentation
- 🟢 Enforce coding standards
- 🟢 Implement accessibility features
- 🎯 Reach 80% test coverage

---

## 👥 Resource Allocation Plan

### Recommended Team Assignment

**Emergency Phase (Weeks 1-2)**:
- 1 Senior Developer → Focus on critical bugs
- 1 Mid-Level Developer → BitmapImage/i18n tasks
- 1 QA Engineer → Validation testing

**Stabilization Phase (Weeks 3-6)**:
- 1 Architect → WidgetManager split design
- 2 Developers → Implementation work
- 1 Tester → Automated test creation

**Modernization Phase (Months 2-3)**:
- Full team participation → Incremental refactoring
- Dedicated test developer → Coverage improvement
- UX Designer → Accessibility requirements

---

## 🔄 Continuous Monitoring

### Metrics to Track

| Metric | Baseline | Target (3mo) | Measurement Method |
|--------|----------|--------------|-------------------|
| Crash rate | ~2%/week | <0.1%/week | Crash reporting service |
| Memory growth | ~2MB/hour | <0.1MB/hour | Performance monitoring |
| Frame rate drop | Occasional | Stable 144fps | FPS counter overlay |
| Support tickets | X/month | Y/month (<X/2) | Helpdesk analytics |
| Dev productivity | Low | High | PR cycle time measurement |

---

## 📞 Escalation Path

### When Issues Block Progress

**Level 1**: Team lead review (same-day resolution preferred)  
**Level 2**: Technical architecture committee (48-hour SLA)  
**Level 3**: Executive decision required (weekly review board)

---

## 📝 Status Definitions

- ✅ **Complete**: Issue resolved, verified, deployed
- 🔴 **In Progress**: Active work underway
- 🟠 **Blocked**: Waiting on external factor
- 🟡 **Backlog**: Scheduled for future sprint
- ⚪ **Not Started**: No work begun yet

---

**Last Updated**: 2026-07-22  
**Next Review**: Daily standup sync  
**Owner**: Project Management Office
