namespace CodexUsageNotifier.Domain.Models;

/// <summary>
/// 1つの利用枠について有効にする通知種類を表します。
/// </summary>
public sealed class RateLimitNotificationSetting
{
    /// <summary>
    /// App Serverが返す利用枠識別子を取得または設定します。
    /// </summary>
    public string LimitId { get; init; } = string.Empty;

    /// <summary>
    /// App Serverレスポンス内の位置を取得または設定します。
    /// </summary>
    public RateLimitPosition Position { get; init; }

    /// <summary>
    /// 利用枠の期間を分単位で取得または設定します。
    /// </summary>
    public int WindowDurationMinutes { get; init; }

    /// <summary>
    /// 短期枠回復通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool ShortWindowRecoveryEnabled { get; init; }

    /// <summary>
    /// 長期枠の早期通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool LongWindowEarlyWarningEnabled { get; init; }

    /// <summary>
    /// 長期枠の通常通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool LongWindowStandardWarningEnabled { get; init; }

    /// <summary>
    /// 長期枠の最終通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool LongWindowFinalWarningEnabled { get; init; }

    /// <summary>
    /// 長期枠のリセット完了通知が有効かどうかを取得または設定します。
    /// </summary>
    public bool LongWindowResetCompletedEnabled { get; init; }

    /// <summary>
    /// いずれかの通知種類が有効かどうかを取得します。
    /// </summary>
    public bool IsAnyEnabled => ShortWindowRecoveryEnabled
        || LongWindowEarlyWarningEnabled
        || LongWindowStandardWarningEnabled
        || LongWindowFinalWarningEnabled
        || LongWindowResetCompletedEnabled;

    /// <summary>
    /// 永続化可能な利用枠識別値かどうかを検証します。
    /// </summary>
    /// <returns>識別値がすべて有効ならtrueです。</returns>
    public bool IsValid()
    {
        return !string.IsNullOrWhiteSpace(LimitId)
            && Enum.IsDefined(Position)
            && WindowDurationMinutes > 0;
    }
}
