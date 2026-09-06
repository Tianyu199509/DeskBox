namespace DeskBox.Services;

public static partial class PerformanceLogger
{
    private static long s_ownedCompositionResourcesCreated;
    private static long s_ownedCompositionResourcesReleased;
    private static long s_contentAppearanceApplied;
    private static long s_contentAppearanceSkipped;
    private static long s_stackProjectionRebuilds;
    private static long s_stackProjectionReuseSkips;

    public static long OwnedCompositionResourcesCreated => Interlocked.Read(ref s_ownedCompositionResourcesCreated);
    public static long OwnedCompositionResourcesReleased => Interlocked.Read(ref s_ownedCompositionResourcesReleased);
    public static long OwnedCompositionResourcesOutstanding =>
        Math.Max(0, OwnedCompositionResourcesCreated - OwnedCompositionResourcesReleased);
    public static long ContentAppearanceApplied => Interlocked.Read(ref s_contentAppearanceApplied);
    public static long ContentAppearanceSkipped => Interlocked.Read(ref s_contentAppearanceSkipped);
    public static long StackProjectionRebuilds => Interlocked.Read(ref s_stackProjectionRebuilds);
    public static long StackProjectionReuseSkips => Interlocked.Read(ref s_stackProjectionReuseSkips);

    internal static void RecordOwnedCompositionResourceCreated() => Interlocked.Increment(ref s_ownedCompositionResourcesCreated);
    internal static void RecordOwnedCompositionResourceReleased() => Interlocked.Increment(ref s_ownedCompositionResourcesReleased);
    internal static void RecordContentAppearanceApplied() => Interlocked.Increment(ref s_contentAppearanceApplied);
    internal static void RecordContentAppearanceSkipped() => Interlocked.Increment(ref s_contentAppearanceSkipped);
    internal static void RecordStackProjectionRebuild() => Interlocked.Increment(ref s_stackProjectionRebuilds);
    internal static void RecordStackProjectionReuseSkip() => Interlocked.Increment(ref s_stackProjectionReuseSkips);
}
