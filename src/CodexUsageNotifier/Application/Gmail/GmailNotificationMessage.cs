namespace CodexUsageNotifier.Application.Gmail;

/// <summary>
/// Gmail APIへ送信する本番利用枠通知の件名と本文を表します。
/// </summary>
public sealed record GmailNotificationMessage
{
    /// <summary>メール件名を取得します。</summary>
    public required string Subject { get; init; }

    /// <summary>UTF-8のプレーンテキスト本文を取得します。</summary>
    public required string Body { get; init; }
}
