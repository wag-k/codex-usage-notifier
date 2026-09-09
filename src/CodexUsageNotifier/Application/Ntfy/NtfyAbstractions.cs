using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Ntfy;

/// <summary>匿名ntfy通知で使用する秘密Topicの生成を抽象化します。</summary>
public interface INtfyTopicGenerator
{
    /// <summary>128ビット以上の乱数を含む新しいTopicを生成します。</summary>
    NtfyTopic Generate();
}

/// <summary>DPAPIで保護されたntfy Topicの永続化を抽象化します。</summary>
public interface INtfyTopicStore
{
    /// <summary>保存済みTopicを読み込みます。未設定時はnullを返します。</summary>
    Task<NtfyTopic?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Topicを原子的に保存します。</summary>
    Task SaveAsync(NtfyTopic topic, CancellationToken cancellationToken);

    /// <summary>保存済みTopicを削除します。</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}

/// <summary>秘密Topic本体を公開せず設定済み状態だけを提供します。</summary>
public interface INtfyTopicConfigurationStatusProvider
{
    /// <summary>復号・検証できるTopicが保存されている場合にtrueを返します。</summary>
    Task<bool> IsConfiguredAsync(CancellationToken cancellationToken);
}

/// <summary>ntfy.shへの通知送信を抽象化します。</summary>
public interface INtfyNotificationSender
{
    /// <summary>秘密TopicをURLへ含めずJSON本文でntfy.shへ送信します。</summary>
    Task SendAsync(NtfyTopic topic, NtfyNotificationMessage message, CancellationToken cancellationToken);
}

/// <summary>本番状態を変更しないntfyテスト通知を提供します。</summary>
public interface INtfyTestNotificationService
{
    /// <summary>保存済みTopicへテスト通知を送信します。</summary>
    Task<NtfyTestNotificationResult> SendAsync(CancellationToken cancellationToken);
}

/// <summary>秘密Topicと、その世代を表す非秘密識別子を保持します。</summary>
public sealed record NtfyTopic
{
    /// <summary>購読に使用する秘密Topic文字列を取得します。</summary>
    public required string Value { get; init; }

    /// <summary>Topic再生成を識別する非秘密の世代IDを取得します。</summary>
    public required string GenerationId { get; init; }

    /// <summary>Topicを生成したUTC時刻を取得します。</summary>
    public DateTimeOffset CreatedAtUtc { get; init; }
}

/// <summary>匿名ntfy Topicが本アプリの安全な形式を満たすか検証します。</summary>
public static class NtfyTopicValidator
{
    /// <summary>Topicが1～64文字の英数字またはハイフンだけで構成されるか判定します。</summary>
    public static bool IsValid(string? value) => !string.IsNullOrWhiteSpace(value)
        && value.Length <= 64
        && value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-');
}

/// <summary>ntfyへ渡す表示用タイトルと本文を保持します。</summary>
public sealed record NtfyNotificationMessage
{
    /// <summary>端末通知に表示するタイトルを取得します。</summary>
    public required string Title { get; init; }

    /// <summary>端末通知に表示するプレーンテキスト本文を取得します。</summary>
    public required string Body { get; init; }
}

/// <summary>状態を変更しないテスト通知の安全な結果を表します。</summary>
public sealed record NtfyTestNotificationResult
{
    /// <summary>ntfy.shがHTTP要求を受理した場合はtrueです。</summary>
    public bool Succeeded { get; init; }

    /// <summary>利用者へ表示できる機密情報を含まない概要を取得します。</summary>
    public required string Message { get; init; }
}

/// <summary>ntfy送信失敗を安全な再試行分類とともに表します。</summary>
public sealed class NtfyDeliveryException : Exception
{
    /// <summary>安全な概要と失敗分類から例外を初期化します。</summary>
    public NtfyDeliveryException(string message, NtfyDeliveryFailureKind failureKind, Exception? innerException = null)
        : base(message, innerException)
    {
        FailureKind = failureKind;
    }

    /// <summary>再試行可否を判断する失敗分類を取得します。</summary>
    public NtfyDeliveryFailureKind FailureKind { get; }
}
