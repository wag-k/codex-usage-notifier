using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Domain.Services;

/// <summary>
/// 現在観測できる利用枠から将来の通知対象を選択します。
/// </summary>
public static class NotificationTargetSelector
{
    /// <summary>
    /// 自動または手動の設定に従って通知対象候補を選択します。
    /// </summary>
    /// <param name="rateLimits">現在観測できるすべての利用枠です。</param>
    /// <param name="selection">通知対象の選択設定です。</param>
    /// <returns>一致した通知対象です。観測できない場合はnullです。</returns>
    public static RateLimitWindow? Select(
        IReadOnlyList<RateLimitWindow> rateLimits,
        NotificationTargetSelection selection)
    {
        ArgumentNullException.ThrowIfNull(rateLimits);
        ArgumentNullException.ThrowIfNull(selection);

        return selection.Mode == NotificationTargetSelectionMode.Manual
            ? SelectManually(rateLimits, selection)
            : SelectAutomatically(rateLimits);
    }

    /// <summary>
    /// 300分枠を優先し、存在しなければ既知の期間が最も短い枠を選択します。
    /// </summary>
    /// <param name="rateLimits">現在観測できるすべての利用枠です。</param>
    /// <returns>自動選択した通知対象です。利用枠がなければnullです。</returns>
    private static RateLimitWindow? SelectAutomatically(IReadOnlyList<RateLimitWindow> rateLimits)
    {
        return rateLimits
            .Where(window => window.WindowDurationMinutes is > 0)
            .OrderBy(window => window.WindowDurationMinutes == 300 ? 0 : 1)
            .ThenBy(window => window.WindowDurationMinutes)
            .ThenBy(window => window.LimitId, StringComparer.Ordinal)
            .ThenBy(window => window.Position)
            .FirstOrDefault();
    }

    /// <summary>
    /// 3つの識別値がすべて一致する利用枠を選択します。
    /// </summary>
    /// <param name="rateLimits">現在観測できるすべての利用枠です。</param>
    /// <param name="selection">手動選択設定です。</param>
    /// <returns>一致した利用枠です。未観測の場合はnullです。</returns>
    private static RateLimitWindow? SelectManually(
        IReadOnlyList<RateLimitWindow> rateLimits,
        NotificationTargetSelection selection)
    {
        if (!selection.IsValid())
        {
            return null;
        }

        return rateLimits.FirstOrDefault(window =>
            string.Equals(window.LimitId, selection.LimitId, StringComparison.Ordinal)
            && window.Position == selection.Position
            && window.WindowDurationMinutes == selection.WindowDurationMinutes);
    }
}
