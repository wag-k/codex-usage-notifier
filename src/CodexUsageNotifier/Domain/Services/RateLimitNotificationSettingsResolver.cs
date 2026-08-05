using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Domain.Services;

/// <summary>
/// 利用枠固有設定を検索し、未設定時は分類別の既定通知設定を生成します。
/// </summary>
public static class RateLimitNotificationSettingsResolver
{
    /// <summary>
    /// 指定利用枠に適用する通知設定を返します。
    /// </summary>
    /// <param name="window">設定を解決する利用枠です。</param>
    /// <param name="settings">分類別既定値と利用枠別上書きを含む設定です。</param>
    /// <returns>完全一致した設定、または分類別の既定設定です。</returns>
    public static RateLimitNotificationSetting Resolve(
        RateLimitWindow window,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);
        RateLimitNotificationSetting? configured = settings.RateLimitNotifications.FirstOrDefault(setting =>
            string.Equals(setting.LimitId, window.LimitId, StringComparison.Ordinal)
            && setting.Position == window.Position
            && setting.WindowDurationMinutes == window.WindowDurationMinutes);
        return configured ?? CreateDefault(window, settings);
    }

    /// <summary>
    /// FiveHour、Weekly、Unknownの分類に対応する既定通知設定を生成します。
    /// </summary>
    /// <param name="window">既定設定の識別値と分類を提供する利用枠です。</param>
    /// <param name="settings">FiveHourとWeeklyの編集可能な既定値です。</param>
    /// <returns>FiveHourは短期回復、Weeklyは全長期通知、Unknownは全通知無効の設定です。</returns>
    public static RateLimitNotificationSetting CreateDefault(RateLimitWindow window, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);
        return new RateLimitNotificationSetting
        {
            LimitId = window.LimitId ?? string.Empty,
            Position = window.Position,
            WindowDurationMinutes = window.WindowDurationMinutes ?? 0,
            ShortWindowRecoveryEnabled = window.Classification == RateLimitClassification.FiveHour
                && settings.ShortWindowRecoveryEnabled,
            LongWindowEarlyWarningEnabled = window.Classification == RateLimitClassification.Weekly
                && settings.LongWindowEarlyWarningEnabled,
            LongWindowStandardWarningEnabled = window.Classification == RateLimitClassification.Weekly
                && settings.LongWindowStandardWarningEnabled,
            LongWindowFinalWarningEnabled = window.Classification == RateLimitClassification.Weekly
                && settings.LongWindowFinalWarningEnabled,
            LongWindowResetCompletedEnabled = window.Classification == RateLimitClassification.Weekly
                && settings.LongWindowResetCompletedEnabled,
        };
    }
}
