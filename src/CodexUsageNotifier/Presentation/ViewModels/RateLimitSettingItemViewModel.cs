using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Presentation.ViewModels;

/// <summary>
/// 設定画面に表示する1つの観測済み利用枠と適用通知設定を表します。
/// </summary>
public sealed class RateLimitSettingItemViewModel
{
    /// <summary>
    /// App Serverが返した利用枠識別子を取得します。
    /// </summary>
    public string LimitId { get; init; } = string.Empty;

    /// <summary>
    /// App Serverレスポンス内の位置を取得します。
    /// </summary>
    public RateLimitPosition Position { get; init; }

    /// <summary>
    /// 利用枠の期間を分単位で取得します。
    /// </summary>
    public int WindowDurationMinutes { get; init; }

    /// <summary>
    /// 利用枠の分類を取得します。
    /// </summary>
    public RateLimitClassification Classification { get; init; }

    /// <summary>
    /// 現在適用される通知種類の表示を取得します。
    /// </summary>
    public string AppliedNotifications { get; init; } = string.Empty;

    /// <summary>
    /// いずれかの通知が有効かどうかを取得します。
    /// </summary>
    public bool IsNotificationEnabled { get; init; }

    /// <summary>
    /// 通知対象状態またはUnknown除外理由の表示を取得します。
    /// </summary>
    public string NotificationStatus { get; init; } = string.Empty;
}
