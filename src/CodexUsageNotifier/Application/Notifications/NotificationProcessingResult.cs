using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>
/// 利用枠通知処理後の状態と、必要な次回確認時刻を表します。
/// </summary>
public sealed class NotificationProcessingResult
{
    /// <summary>
    /// 永続化された最新状態を取得または設定します。
    /// </summary>
    public required ApplicationState State { get; init; }

    /// <summary>
    /// 通知禁止時間終了時に再取得すべきUTC時刻を取得または設定します。
    /// </summary>
    public DateTimeOffset? DeferredUntilUtc { get; init; }
}
