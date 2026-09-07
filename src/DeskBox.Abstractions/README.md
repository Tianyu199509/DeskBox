# DeskBox.Abstractions

Internal host contract assembly for the widget content seam
(`IWidgetContent` family, `WidgetConfig`, `WidgetContentDescriptor`,
chrome enums, calendar presentation models).

**This is NOT the public extension/plugin SDK.**

- It exists so first-party built-in features and the host share one
  physical assembly while the pluginization program runs
  (docs/architecture/pluginization-roadmap.md, stage 1).
- It deliberately contains host-persistence models (`WidgetConfig`
  mixes host-common state with File-widget-specific fields) and WinUI
  types (`FrameworkElement`), which is exactly what a third-party
  extension model must never see.
- The future third-party/AI-facing layer is a separate, language-neutral
  Extension Model (roadmap section 14): plain DTOs, no WinAppSDK, no
  WinUI, no host types - projected to JSON Schema / WIT / RPC adapters.

Rules of engagement:

- Move types in only when a real dependency demands it, as a minimal
  interface - never "because a plugin might use it someday".
- Do not grow `WidgetConfig`; new per-feature state belongs in feature
  stores or the future plugin payload split.
- Host namespaces are kept (`DeskBox.Models/Contracts/Services`) for
  zero-churn migration; future platform types get their own namespaces.
