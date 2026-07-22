# 技术债务优先级路线图 (2026 Q3-Q4)

## 🎯 执行摘要

基于全面代码审查发现的**52个具体问题**，制定以下分阶段修复计划。预计总投入 **154 人时**，可在 6 个月内将项目健康度从 5.2/10提升至 8.0/10。

---

## 📅 时间线概览

```
July 2026     August 2026    September 2026   October 2026
[PHASE 1]     [PHASE 2]      [PHASE 3]        [PHASE 4]
Emergency     Stability      Architecture     Future-Proofing
Fixes         Hardening      Modernization    & Scale
│             │              │                │
│─Critical   │─Memory       │─Refactor       │─i18n Full
bugs fixed   │─leaksfixed  │─servicesplit   │─Localization
│─Resource   │─Performance  │─Testing↑       │─RTL support
 cleanup     │optimization │─Coverage>80%   │─Accessibility
│─i18nbasic  │─Deadlockfix │─Docscomplete  │─AutomatedQA
 setup       │            │              │
```

---

## 🔴 Phase 1: Emergency Fixes (Week 1-2)
**Budget**: 20 人时  
**Goal**: Eliminate all 🔴 Critical issues  

### Deliverables Checklist

#### ✅ Week 1 (Days 1-5)

- [ ] **[P0]** MusicSessionService.Dispose() implementation
  - File: `src/DeskBox/Services/MusicSessionService.cs`
  - Task: Add IDisposable + release COM objects
  - Owner: Senior Developer
  - Time: 1h

- [ ] **[P0]** CompositionTarget exception handling
  - File: `src/DeskBox/Services/WidgetTrayAnimationController.cs:L427`
  - Task: Wrap OnRenderingFrame in try-catch
  - Owner: Any Developer
  - Time: 0.5h

- [ ] **[P0]** Atomic state persistence
  - File: `src/DeskBox/Services/WidgetManager.Storage.cs`
  - Task: Implement two-phase file write
  - Owner: Backend Developer
  - Time: 2h

- [ ] **[P1]** BitmapImage memory leak fixes
  - Scope: All views using images (~15 files)
  - Task: Replace with using statements
  - Owner: Junior Developers (2 people)
  - Time: 4h total

- [ ] **[CRITICAL]** i18n infrastructure foundation
  - Task: Create Resources folder + .resx files
  - Deliverable: Strings.zh-CN.resx, Strings.en-US.resx
  - Owner: Lead Developer
  - Time: 6h

#### ✅ Week 2 (Days 6-10)

- [ ] **[P1]** GDI handle leak prevention
  - Files: Any Win32 API calls in Z-Order APIs
  - Task: Add IntPtr validation + try-catch
  - Owner: Platform Engineer
  - Time: 2h

- [ ] **[P1]** Stream/Reader disposal audit
  - Scope: SearchEngine, IndexedFileService
  - Task: Verify all streams closed properly
  - Owner: QA Engineer + Dev
  - Time: 3h

- [ ] **[P1]** Event subscription cleanup pattern
  - Scope: All event handlers across services
  - Task: Ensure unsubscribe on dispose
  - Owner: All developers
  - Time: 3h total

**Phase 1 Exit Criteria**:
- ✅ Zero 🔴 Critical issues remaining
- ✅ 100% resource cleanup coverage verified
- ✅ Chinese/English language switching functional

---

## 🟠 Phase 2: Stability Hardening (Week 3-6)
**Budget**: 40 人时  
**Goal**: Address high-priority stability and performance  

### Key Initiatives

#### Initiative 2.1: Memory Optimization (Week 3)
**Owner**: Performance Engineer  
**Time**: 10h  

- [ ] Profile memory usage with Application Verifier
- [ ] Fix BitmapImage caching strategy
- [ ] Optimize ListView virtualization
- [ ] Reduce idle memory from 80MB to 50MB

**Deliverable**: Memory benchmark report showing 37.5% reduction

---

#### Initiative 2.2: Threading Safety Audit (Week 4)
**Owner**: Senior Architect  
**Time**: 12h  

- [ ] Analyze SettingsService deadlock scenario
- [ ] Implement proper lock ordering
- [ ] Add async/await everywhere possible
- [ ] Remove fire-and-forget async void methods

**Deliverable**: Thread safety documentation + zero unhandled deadlocks

---

#### Initiative 2.3: Drag Performance Enhancement (Week 5)
**Owner**: Graphics Programmer  
**Time**: 8h  

- [ ] Implement batched layout updates
- [ ] Debounce drag events at 60fps
- [ ] Optimize WidgetCapsuleArrangement calculations
- [ ] Test across 60Hz/144Hz/165Hz displays

**Deliverable**: Smooth drag experience at 60fps sustained

---

#### Initiative 2.4: i18n Expansion (Week 6)
**Owner**: Localization Manager  
**Time**: 10h  

- [ ] Extract all UI strings from XAML
- [ ] Migrate MessageBox.Show() calls
- [ ] Implement Culture-aware formatting
- [ ] Test with zh-CN, en-US cultures

**Deliverable**: Fully localizable application ready for external translators

---

## 🟡 Phase 3: Architecture Modernization (Month 2-3)
**Budget**: 80 人时  
**Goal**: Refactor core components for maintainability  

### Major Refactoring Projects

#### Project 3.1: WidgetManager Split (Month 2)
**Owner**: System Architect  
**Time**: 30h  

**New Structure**:
```
WidgetManager (Orchestrator only)
├── IWidgetLifecycleManager
├── IWidgetLayoutCalculator
├── IWidgetAnimationController
└── IWidgetStorageService
```

**Tasks**:
- [ ] Extract capsule arrangement logic → CapsuleArrangementService
- [ ] Extract tray animation → TrayAnimationService
- [ ] Extract storage logic → WidgetStateRepository
- [ ] Wire up DI container with new interfaces
- [ ] Write unit tests for each service (target: 80% coverage)

**Success Metric**: WidgetManager lines reduced from ~1100 to <400

---

#### Project 3.2: Dependency Injection Overhaul (Month 2)
**Owner**: DI Specialist  
**Time**: 20h  

**Goals**:
- Eliminate static ServiceRegistry
- Introduce IServiceRegistry interface
- Enable constructor injection everywhere

**Tasks**:
- [ ] CreateIServiceRegistry abstraction
- [ ] Migrate all services to constructor injection
- [ ] Add AutoFixture for test data generation
- [ ] Remove all Static method calls in production code

**Expected Impact**: Unit test execution time reduced by 80%

---

#### Project 3.3: Factory Pattern Modernization (Month 3)
**Owner**: Design Patterns Expert  
**Time**: 15h  

**Problem**: WidgetContentFactory violates Open/Closed Principle

**Solution**: Strategy Pattern + MEF Export

**Tasks**:
- [ ] Define IWidgetProvider interface
- [ ] Convert each widget type to provider
- [ ] Use [Export] attribute for auto-discovery
- [ ] Remove switch statements in factory

**Benefit**: Third-party widgets can be added without modifying core code

---

#### Project 3.4: Testing Infrastructure (Month 3)
**Owner**: QA Automation Lead  
**Time**: 15h  

**Objectives**:
- Achieve >80% code coverage
- Setup CI integration
- Add performance regression tests

**Tasks**:
- [ ] Configure Coverlet for coverage reporting
- [ ] Add xUnit tests for all ViewModels
- [ ] Create IntegrationTest suite
- [ ] Benchmark suite for frame rate validation
- [ ] GitHub Actions pipeline integration

**KPI**: PRs blocked if coverage drops below 75%

---

## 🟢 Phase 4: Future-Proofing (Month 4+)
**Budget**: Variable  
**Goal**: Prepare for long-term sustainability  

### Strategic Initiatives

#### Initiative 4.1: Full i18n Localization (Q3)
**Scope**: 10+ languages  
**Timeline**: Ongoing  

**Milestones**:
- Month 4: Setup Crowdin/Weblate integration
- Month 5: Launch community translation campaign
- Month 6: Release v1.4 with EN/FR/DE/ES/JP support

**Investment**: 20h initial setup + 5h/month maintenance

---

#### Initiative 4.2: Accessibility Compliance (Q4)
**Standard**: WCAG 2.1 Level AA  
**Timeline**: 6 weeks  

**Focus Areas**:
- Keyboard navigation (Tab order, shortcuts)
- Screen reader support (ARIA labels)
- High contrast mode compatibility
- Text scaling up to 200%

**Effort**: 40h with accessibility consultant guidance

---

#### Initiative 4.3: Automated Quality Gates (Q4)
**Tools**: SonarQube + Azure DevOps  
**Deadline**: End of Q4  

**Rulesets**:
- No new code smells
- Duplication <3%
- Security hotspot resolution <7 days
- Code review mandatory for all PRs

**Setup Effort**: 10h

---

## 💵 Budget Summary

| Phase | Timeline | Cost (Hours) | Role Allocation | Estimated Cost* |
|-------|----------|--------------|-----------------|-----------------|
| Phase 1 | Month 1 | 20h | 1 Senior Dev | $4,000 |
| Phase 2 | Month 1-2 | 40h | 1 Senior + 1 Mid | $8,000 |
| Phase 3 | Month 2-3 | 80h | 2 Seniors | $20,000 |
| Phase 4 | Month 3-6 | 100h | Team-wide | $30,000 |
| **Total** | **6 months** | **240h** | **Full team** | **$62,000** |

*Assuming $200/hr blended rate

---

## 📈 Success Measurement

### Quantitative KPIs

| Metric | Baseline | Target (Month 3) | Target (Month 6) |
|--------|----------|------------------|------------------|
| Overall Health Score | 5.2/10 | 6.8/10 | 8.2/10 |
| Critical Issues | 3 | 0 | 0 |
| Memory (Idle) | 80MB | 60MB | 40MB |
| Frame Rate | 60fps | 90fps | 144fps |
| Test Coverage | <10% | 50% | 85% |
| Build Duration | 5min | 3min | 2min |
| Languages Supported | 1 | 2 | 10+ |

### Qualitative Wins

✅ Developer satisfaction survey >8/10  
✅ Onboarding new dev <1 week  
✅ Customer support tickets ↓ by 50%  
✅ App Store rating ↑ to 4.5+ stars  

---

## ⚖️ Risk Mitigation

### Technical Risks

| Risk | Probability | Severity | Contingency Plan |
|------|-----------|----------|------------------|
| Legacy static code blocks testing | High | Medium | Phased migration over 2 sprints |
| Performance regression during refactor | Medium | High | Maintain benchmark suite before every commit |
| i18n string explosion (too many keys) | Low | Low | Establish naming convention early |

### Business Risks

| Risk | Probability | Severity | Contingency |
|------|-----------|----------|-------------|
| Competitor launches better perf | Medium | High | Continue GPU optimization track parallel |
| User demands more languages ASAP | High | Medium | Prioritize top 5 markets first |
| Key developer leaves mid-project | Low | High | Document everything, pair programming culture |

---

## 🎬 Quick Start Guide

### For Project Managers

**Week 1 Tasks**:
1. Review this roadmap with team
2. Create GitHub issues for all P0 items
3. Assign owners and set due dates
4. Schedule daily standup for emergency fixes

**Resources Needed**:
- 1 Senior Developer (full-time Weeks 1-2)
- 1 Mid Developer (part-time Weeks 1-6)
- Access to profiling tools (Application Verifier, PerfView)

---

### For Developers

**Immediate Action Items**:
```bash
# 1. Clone repo and create feature branch
git checkout -b feat/critical-fixes-phase1

# 2. Run baseline benchmarks
dotnet test --filter "Category=Performance"

# 3. Start with MusicSessionService.Dispose()
# See docs/audit/PART1-ARCHITECTURE/4-threading-model.md

# 4. Submit PR with screenshot before/after metrics
```

---

### For QA Team

**Testing Preparation**:
- [ ] Setup AppVerifier for memory leak detection
- [ ] Configure帧率监控工具（MSI Afterburner）
- [ ] Prepare i18n test scenarios (CN/EN toggle)
- [ ] Automate smoke tests for critical paths

---

## 🔗 Related Documentation

Detailed findings referenced in this roadmap:
- [`0-summary-and-executive-summary.md`](./0-summary-and-executive-summary.md) - Full audit results
- [`PART1-ARCHITECTURE/1-project-architecture.md`](./PART1-ARCHITECTURE/1-project-architecture.md) - Module boundaries
- [`PART1-ARCHITECTURE/2-dependency-injection-audit.md`](./PART1-ARCHITECTURE/2-dependency-injection-audit.md) - DI issues
- [`PART1-ARCHITECTURE/4-threading-model.md`](./PART1-ARCHITECTURE/4-threading-model.md) - Memory leaks
- [`PART2-FUNCTIONS/7-widget-manager.md`](./PART2-FUNCTIONS/7-widget-manager.md) - WidgetManager analysis
- [`PART5-I18N/44-i18n-strategy.md`](./PART5-I18N/44-i18n-strategy.md) - i18n implementation guide

---

**Roadmap Version**: v1.0  
**Approved By**: [Pending Stakeholder Sign-off]  
**Last Updated**: 2026-07-22  
**Next Review**: Weekly sync every Monday 10:00 AM
