namespace CodexUsageNotifier.Application.Notifications;

/// <summary>
/// Windows通知へ渡す機密情報を含まないタイトルと本文を表します。
/// </summary>
public sealed class WindowsNotificationMessage
{
    /// <summary>
    /// 通知タイトルを取得または設定します。
    /// </summary>
    public required string Title { get; init; }

    /// <summary>
    /// 通知本文を取得または設定します。
    /// </summary>
    public required string Body { get; init; }
}
