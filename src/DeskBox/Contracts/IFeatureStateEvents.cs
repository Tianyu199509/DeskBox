namespace DeskBox.Contracts;

/// <summary>
/// Host-internal event port for feature enable-state changes (pluginization
/// roadmap stage 3, cut point 3). Extracted from the
/// SetFeatureWidgetEnabledState switch that called App.Current service
/// refresh methods directly, creating an App-to-feature ambient dependency
/// the architecture ratchet tracks.
///
/// Host-internal port, NOT the future public extension capability API and
/// NOT a future plugin event bus: extension eventing needs subscriber
/// isolation, backpressure and runtime-scoped subscriptions on top of a
/// broker (roadmap section 5); this port only removes the ambient
/// App.Current dependency for built-in single-process features.
///
/// Named FeatureStateEvents rather than Lifecycle: the roadmap pins three
/// separate lifecycles (package / runtime / widget instance) and this port
/// carries only enable-state transitions.
///
/// Event contract the stage 3d hub must implement:
/// - events are raised on the UI thread (state changes originate there);
/// - subscriber exceptions are caught and logged per subscriber, so one
///   failing listener never breaks the others;
/// - subscribers own their unsubscribe; a feature that shuts down or is
///   disabled must not leave dangling subscriptions behind.
/// </summary>
public interface IFeatureStateEvents
{
    /// <summary>Raised when a feature's enabled state changes.</summary>
    event Action<FeatureStateChangedEventArgs>? FeatureStateChanged;
}

/// <summary>Carries the feature and its new enabled state.</summary>
public sealed record FeatureStateChangedEventArgs(
    FeatureId FeatureId,
    bool Enabled);

/// <summary>
/// Stable host feature identifier - the bridge from the WidgetKind enum
/// world to stable ids (plugin-era code must not depend on host enums).
/// Values are pinned by <see cref="DeskBoxFeatureIds"/>.
/// </summary>
public readonly record struct FeatureId(string Value);

/// <summary>
/// Stable ids for the built-in features. Host-side aggregation only: never
/// move into Abstractions and never extend from third-party code - external
/// contribution ids derive from package ids, not from this registry.
/// </summary>
public static class DeskBoxFeatureIds
{
    public static readonly FeatureId Todo = new("todo");
    public static readonly FeatureId Search = new("search");
    public static readonly FeatureId QuickCapture = new("quick-capture");
    public static readonly FeatureId Music = new("music");
    public static readonly FeatureId Weather = new("weather");
    public static readonly FeatureId Glance = new("glance");
}
