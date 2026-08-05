using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>
/// 本番の通知状態や利用枠履歴を変更せず、指定種類のWindows通知だけを送信します。
/// </summary>
public sealed partial class TestNotificationService
{
    private readonly IWindowsNotificationSender windowsNotificationSender;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<TestNotificationService> logger;

    /// <summary>
    /// Windows通知送信先、時刻、および診断ロガーを受け取ります。
    /// </summary>
    /// <param name="windowsNotificationSender">テスト通知の送信先です。</param>
    /// <param name="timeProvider">サンプル時刻を生成する時刻提供元です。</param>
    /// <param name="logger">送信結果の記録先です。</param>
    public TestNotificationService(
        IWindowsNotificationSender windowsNotificationSender,
        TimeProvider timeProvider,
        ILogger<TestNotificationService> logger)
    {
        ArgumentNullException.ThrowIfNull(windowsNotificationSender);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.windowsNotificationSender = windowsNotificationSender;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 指定通知種類のサンプルをWindowsへ送信し、永続状態は更新しません。
    /// </summary>
    /// <param name="notificationType">送信するテスト通知の種類です。</param>
    /// <param name="cancellationToken">送信のキャンセル通知です。</param>
    /// <returns>送信完了を表す非同期処理です。</returns>
    public async Task SendAsync(
        RateLimitNotificationType notificationType,
        CancellationToken cancellationToken)
    {
        DateTimeOffset nowUtc = timeProvider.GetUtcNow();
        RateLimitNotificationCandidate candidate = CreateCandidate(notificationType, nowUtc);
        WindowsNotificationMessage message = WindowsNotificationMessageFactory.Create(candidate, nowUtc);
        try
        {
            await windowsNotificationSender.SendAsync(message, cancellationToken);
            LogTestNotificationSucceeded(logger, notificationType);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogTestNotificationFailed(logger, notificationType, exception);
            throw;
        }
    }

    /// <summary>
    /// 通知種類に対応するテスト用利用枠と通知候補を生成します。
    /// </summary>
    /// <param name="notificationType">生成する通知種類です。</param>
    /// <param name="nowUtc">サンプルの基準UTC時刻です。</param>
    /// <returns>本番状態へ保存しないテスト用通知候補です。</returns>
    internal static RateLimitNotificationCandidate CreateCandidate(
        RateLimitNotificationType notificationType,
        DateTimeOffset nowUtc)
    {
        (RateLimitClassification classification, int duration, RateLimitNotificationStage stage) =
            notificationType switch
            {
                RateLimitNotificationType.ShortWindowRecovered =>
                    (RateLimitClassification.FiveHour, 300, RateLimitNotificationStage.Recovered),
                RateLimitNotificationType.LongWindowEarlyWarning =>
                    (RateLimitClassification.Weekly, 10080, RateLimitNotificationStage.Early),
                RateLimitNotificationType.LongWindowStandardWarning =>
                    (RateLimitClassification.Weekly, 10080, RateLimitNotificationStage.Standard),
                RateLimitNotificationType.LongWindowFinalWarning =>
                    (RateLimitClassification.Weekly, 10080, RateLimitNotificationStage.Final),
                RateLimitNotificationType.LongWindowResetCompleted =>
                    (RateLimitClassification.Weekly, 10080, RateLimitNotificationStage.Completed),
                RateLimitNotificationType.MonitoringFailure =>
                    (RateLimitClassification.Unknown, 0, RateLimitNotificationStage.None),
                _ => throw new ArgumentOutOfRangeException(
                    nameof(notificationType),
                    notificationType,
                    "テスト通知に対応していない通知種類です。"),
            };
        return new RateLimitNotificationCandidate
        {
            Window = new RateLimitWindow
            {
                LimitId = "test",
                LimitName = "テスト通知",
                Position = RateLimitPosition.Primary,
                Classification = classification,
                WindowDurationMinutes = duration,
                UsedPercent = 35,
                RemainingPercent = 65,
                ResetsAtUtc = nowUtc.AddHours(24),
            },
            RecoveryWindowId = "test-notification",
            NotificationType = notificationType,
            NotificationStage = stage,
            ConditionMetAtUtc = nowUtc,
            ResetCompletionReason = notificationType == RateLimitNotificationType.LongWindowResetCompleted
                ? RateLimitResetCompletionReason.ResetTimeAdvanced
                : null,
        };
    }

    [LoggerMessage(2400, LogLevel.Information, "テスト通知を送信しました。NotificationType={NotificationType}")]
    private static partial void LogTestNotificationSucceeded(
        ILogger logger,
        RateLimitNotificationType notificationType);

    [LoggerMessage(2401, LogLevel.Error, "テスト通知を送信できませんでした。NotificationType={NotificationType}")]
    private static partial void LogTestNotificationFailed(
        ILogger logger,
        RateLimitNotificationType notificationType,
        Exception exception);
}
