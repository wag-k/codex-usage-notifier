using CodexUsageNotifier.Application.Ntfy;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>本番通知状態へ触れず、保存済みTopicへntfyテスト通知を送信します。</summary>
public sealed partial class NtfyTestNotificationService : INtfyTestNotificationService
{
    private readonly INtfyTopicStore topicStore;
    private readonly INtfyNotificationSender sender;
    private readonly ILogger<NtfyTestNotificationService> logger;

    [LoggerMessage(EventId = 3111, Level = LogLevel.Information, Message = "ntfyテスト通知がHTTP APIで受理されました。")]
    private static partial void LogTestSucceeded(ILogger logger);

    [LoggerMessage(EventId = 3112, Level = LogLevel.Warning, Message = "ntfyテスト通知を送信できませんでした。Topicや本文は記録していません。")]
    private static partial void LogTestFailed(ILogger logger, Exception exception);

    /// <summary>Topicストア、送信先、および安全なログ出力先を受け取ります。</summary>
    public NtfyTestNotificationService(INtfyTopicStore topicStore, INtfyNotificationSender sender, ILogger<NtfyTestNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(topicStore);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(logger);
        this.topicStore = topicStore;
        this.sender = sender;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<NtfyTestNotificationResult> SendAsync(CancellationToken cancellationToken)
    {
        NtfyTopic? topic = await topicStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (topic is null)
        {
            return new NtfyTestNotificationResult { Message = "先にスマホ通知Topicを生成してください。" };
        }

        try
        {
            await sender.SendAsync(topic, new NtfyNotificationMessage
            {
                Title = "テスト通知｜週間残り65%",
                Body = "次回リセット 2026/01/01 12:00\n\nCodex Usage Notifierのntfy通知設定テストです。\n通知種類: テスト\n週間枠残量: 65%\nテストデータ",
            }, cancellationToken).ConfigureAwait(false);
            LogTestSucceeded(logger);
            return new NtfyTestNotificationResult
            {
                Succeeded = true,
                Message = "テスト通知を送信しました。ntfyが要求を受理しました。端末での受信を確認してください。",
            };
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTestFailed(logger, exception);
            return new NtfyTestNotificationResult { Message = exception.Message };
        }
    }
}
