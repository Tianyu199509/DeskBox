using DeskBox.Models;

namespace DeskBox.Services;

/// <summary>
/// 番茄钟全局配置的默认值与边界策略。
/// 配置值在加载和保存前都经过这里归一化，避免状态机、设置页各自实现
/// 不一致的校验逻辑。
/// </summary>
public static class PomodoroSettingsPolicy
{
    /// <summary>默认周期轮数。</summary>
    public const int DefaultRoundCount = 4;

    /// <summary>周期轮数的最小值。</summary>
    public const int MinRoundCount = 1;

    /// <summary>周期轮数的最大值。</summary>
    public const int MaxRoundCount = 12;

    /// <summary>默认专注时长（分钟）。</summary>
    public const int DefaultFocusMinutes = 25;

    /// <summary>专注时长的最小值（分钟）。</summary>
    public const int MinFocusMinutes = 1;

    /// <summary>专注时长的最大值（分钟）。</summary>
    public const int MaxFocusMinutes = 120;

    /// <summary>默认短休息时长（分钟）。</summary>
    public const int DefaultShortBreakMinutes = 5;

    /// <summary>短休息时长的最小值（分钟）。</summary>
    public const int MinShortBreakMinutes = 1;

    /// <summary>短休息时长的最大值（分钟）。</summary>
    public const int MaxShortBreakMinutes = 60;

    /// <summary>默认长休息时长（分钟）。</summary>
    public const int DefaultLongBreakMinutes = 15;

    /// <summary>长休息时长的最小值（分钟）。</summary>
    public const int MinLongBreakMinutes = 1;

    /// <summary>长休息时长的最大值（分钟）。</summary>
    public const int MaxLongBreakMinutes = 60;

    /// <summary>默认启用阶段完成提示音。</summary>
    public const bool DefaultCompletionSoundEnabled = true;

    /// <summary>默认启用阶段完成系统通知。</summary>
    public const bool DefaultCompletionNotificationEnabled = true;

    /// <summary>
    /// 将周期轮数限制在支持的范围内。
    /// </summary>
    public static int NormalizeRoundCount(int value) =>
        Math.Clamp(value, MinRoundCount, MaxRoundCount);

    /// <summary>
    /// 将专注时长限制在支持的范围内。
    /// </summary>
    public static int NormalizeFocusMinutes(int value) =>
        Math.Clamp(value, MinFocusMinutes, MaxFocusMinutes);

    /// <summary>
    /// 将短休息时长限制在支持的范围内。
    /// </summary>
    public static int NormalizeShortBreakMinutes(int value) =>
        Math.Clamp(value, MinShortBreakMinutes, MaxShortBreakMinutes);

    /// <summary>
    /// 将长休息时长限制在支持的范围内。
    /// </summary>
    public static int NormalizeLongBreakMinutes(int value) =>
        Math.Clamp(value, MinLongBreakMinutes, MaxLongBreakMinutes);

    /// <summary>
    /// 归一化番茄钟配置，并返回配置是否发生变化。
    /// </summary>
    public static bool Normalize(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        int roundCount = NormalizeRoundCount(settings.PomodoroRoundCount);
        int focusMinutes = NormalizeFocusMinutes(settings.PomodoroFocusMinutes);
        int shortBreakMinutes = settings.LegacyPomodoroBreakMinutes is { } legacyBreakMinutes
            ? NormalizeShortBreakMinutes(legacyBreakMinutes)
            : NormalizeShortBreakMinutes(settings.PomodoroShortBreakMinutes);
        int longBreakMinutes = NormalizeLongBreakMinutes(
            settings.PomodoroLongBreakMinutes);
        bool changed = false;

        if (settings.PomodoroRoundCount != roundCount)
        {
            settings.PomodoroRoundCount = roundCount;
            changed = true;
        }

        if (settings.PomodoroFocusMinutes != focusMinutes)
        {
            settings.PomodoroFocusMinutes = focusMinutes;
            changed = true;
        }

        if (settings.PomodoroShortBreakMinutes != shortBreakMinutes)
        {
            settings.PomodoroShortBreakMinutes = shortBreakMinutes;
            changed = true;
        }

        if (settings.PomodoroLongBreakMinutes != longBreakMinutes)
        {
            settings.PomodoroLongBreakMinutes = longBreakMinutes;
            changed = true;
        }

        if (settings.LegacyPomodoroBreakMinutes is not null)
        {
            settings.LegacyPomodoroBreakMinutes = null;
            changed = true;
        }

        return changed;
    }
}
