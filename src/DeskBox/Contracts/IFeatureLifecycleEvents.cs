namespace DeskBox.Contracts;

/// <summary>
/// Host-level events that feature code subscribes to instead of reaching
/// into App.Current (pluginization roadmap stage 3, cut point 3). Extracted
/// from the SetFeatureWidgetEnabledState switch that called App.Current
/// service refresh methods directly, creating an App-to-feature ambient
/// dependency that the architecture ratchet tracks.
/// </summary>
public interface IFeatureLifecycleEvents
{
    /// <summary>Raised when a feature's enabled state changes.</summary>
    event Action<FeatureStateChangedEventArgs>? FeatureStateChanged;
}

/// <summary>Carries the feature and its new enabled state.</summary>
public sealed record FeatureStateChangedEventArgs(
    string FeatureId,
    bool Enabled);
