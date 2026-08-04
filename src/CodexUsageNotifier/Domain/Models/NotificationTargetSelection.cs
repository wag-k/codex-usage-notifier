namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// 将来の回復通知で監視対象にする利用枠の選択設定を表します。
/// </summary>
public sealed class NotificationTargetSelection
{
    /// <summary>
    /// 利用枠の選択方法を取得または設定します。
    /// </summary>
    public NotificationTargetSelectionMode Mode { get; init; } = NotificationTargetSelectionMode.Automatic;

    /// <summary>
    /// 手動選択する利用枠識別子を取得または設定します。
    /// </summary>
    public string? LimitId { get; init; }

    /// <summary>
    /// 手動選択するレスポンス内の位置を取得または設定します。
    /// </summary>
    public RateLimitPosition? Position { get; init; }

    /// <summary>
    /// 手動選択するウィンドウ長を分単位で取得または設定します。
    /// </summary>
    public int? WindowDurationMinutes { get; init; }

    /// <summary>
    /// 設定値が選択方法に対して有効かどうかを判定します。
    /// </summary>
    /// <returns>自動選択、または手動選択の全識別値が有効ならtrueです。</returns>
    public bool IsValid()
    {
        return Mode switch
        {
            NotificationTargetSelectionMode.Automatic => true,
            NotificationTargetSelectionMode.Manual =>
                !string.IsNullOrWhiteSpace(LimitId)
                && Position is not null
                && Enum.IsDefined(Position.Value)
                && WindowDurationMinutes > 0,
            _ => false,
        };
    }
}

/// <summary>
/// 通知対象の利用枠を選ぶ方法を表します。
/// </summary>
public enum NotificationTargetSelectionMode
{
    /// <summary>
    /// 300分枠、次いで最短期間の枠を自動選択します。
    /// </summary>
    Automatic,

    /// <summary>
    /// LimitId、Position、WindowDurationMinutesで手動選択します。
    /// </summary>
    Manual
}
