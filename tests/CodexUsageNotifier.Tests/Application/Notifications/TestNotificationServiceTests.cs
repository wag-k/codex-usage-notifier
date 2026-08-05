using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Application.Notifications;

/// <summary>
/// テスト通知が本番状態から独立し、指定種類を個別送信できることを検証します。
/// </summary>
[TestClass]
public sealed class TestNotificationServiceTests
{
    /// <summary>
    /// 6種類を個別送信しても既存の通知済み状態オブジェクトを変更しないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task SendAsync_AllSupportedTypes_DoesNotChangeProductionState()
    {
        ApplicationState state = new()
        {
            RateLimitNotificationStates =
            [
                new RateLimitNotificationState
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = 300,
                    RecoveryWindowId = "reset:1",
                    NotificationType = RateLimitNotificationType.ShortWindowRecovered,
                    NotificationStage = RateLimitNotificationStage.Recovered,
                    WindowsDeliveryStatus = DeliveryStatus.Succeeded,
                },
            ],
        };
        RecordingSender sender = new();
        TestNotificationService service = new(
            sender,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero)),
            NullLogger<TestNotificationService>.Instance);
        RateLimitNotificationType[] types =
        [
            RateLimitNotificationType.ShortWindowRecovered,
            RateLimitNotificationType.LongWindowEarlyWarning,
            RateLimitNotificationType.LongWindowStandardWarning,
            RateLimitNotificationType.LongWindowFinalWarning,
            RateLimitNotificationType.LongWindowResetCompleted,
            RateLimitNotificationType.MonitoringFailure,
        ];

        foreach (RateLimitNotificationType type in types)
        {
            await service.SendAsync(type, CancellationToken.None);
        }

        Assert.AreEqual(6, sender.Messages.Count);
        Assert.AreEqual(1, state.RateLimitNotificationStates.Count);
        Assert.AreEqual("reset:1", state.RateLimitNotificationStates.Single().RecoveryWindowId);
    }

    /// <summary>
    /// Windows通知メッセージをメモリへ記録する送信先です。
    /// </summary>
    private sealed class RecordingSender : IWindowsNotificationSender
    {
        /// <summary>
        /// 送信された通知メッセージを取得します。
        /// </summary>
        public List<WindowsNotificationMessage> Messages { get; } = [];

        /// <summary>
        /// 通知メッセージを記録します。
        /// </summary>
        /// <param name="message">記録する通知です。</param>
        /// <param name="cancellationToken">送信のキャンセル通知です。</param>
        /// <returns>完了済み処理です。</returns>
        public Task SendAsync(WindowsNotificationMessage message, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Add(message);
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 固定UTC時刻を返すテスト用時刻提供元です。
    /// </summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        /// <summary>
        /// 固定するUTC時刻を受け取ります。
        /// </summary>
        /// <param name="utcNow">返却するUTC時刻です。</param>
        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        /// <summary>
        /// 固定UTC時刻を返します。
        /// </summary>
        /// <returns>コンストラクターで指定した時刻です。</returns>
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
