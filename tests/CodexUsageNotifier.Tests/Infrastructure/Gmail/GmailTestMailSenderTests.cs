using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Gmail;
using CodexUsageNotifier.Tests.TestDoubles;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Infrastructure.Gmail;

/// <summary>
/// Gmailテストメールの送信条件、同時実行抑止、および本番状態非変更を検証します。
/// </summary>
[TestClass]
public sealed class GmailTestMailSenderTests
{
    /// <summary>認証済みかつ有効な送信先の場合だけAPIを呼び出すことを検証します。</summary>
    [TestMethod]
    public async Task SendAsync_AuthenticatedAndValidRecipient_SendsMessage()
    {
        StubGmailAuthenticationService authentication = CreateAuthenticatedStatus();
        RecordingApiClient api = new();
        GmailTestMailSender sender = CreateSender(authentication, api);

        GmailOperationResult result = await sender.SendAsync("target@example.com", CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, api.SendCount);
        Assert.IsNotNull(api.LastRawMessage);
    }

    /// <summary>未認証ではAPIを呼び出さないことを検証します。</summary>
    [TestMethod]
    public async Task SendAsync_Unauthenticated_DoesNotSend()
    {
        StubGmailAuthenticationService authentication = new();
        RecordingApiClient api = new();
        GmailTestMailSender sender = CreateSender(authentication, api);

        GmailOperationResult result = await sender.SendAsync("target@example.com", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, api.SendCount);
    }

    /// <summary>不正な送信先ではAPIを呼び出さないことを検証します。</summary>
    [TestMethod]
    public async Task SendAsync_InvalidRecipient_DoesNotSend()
    {
        RecordingApiClient api = new();
        GmailTestMailSender sender = CreateSender(CreateAuthenticatedStatus(), api);

        GmailOperationResult result = await sender.SendAsync("invalid-address", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(0, api.SendCount);
    }

    /// <summary>テスト送信成功時に本番通知状態、回復連番、履歴を変更しないことを検証します。</summary>
    [TestMethod]
    public async Task SendAsync_Success_DoesNotChangeProductionState()
    {
        ApplicationState state = CreateProductionState();
        string before = System.Text.Json.JsonSerializer.Serialize(state);
        GmailTestMailSender sender = CreateSender(CreateAuthenticatedStatus(), new RecordingApiClient());

        await sender.SendAsync("target@example.com", CancellationToken.None);

        Assert.AreEqual(before, System.Text.Json.JsonSerializer.Serialize(state));
        Assert.AreEqual(DeliveryStatus.Succeeded, state.RateLimitNotificationStates.Single().WindowsDeliveryStatus);
        Assert.AreEqual(2, state.RateLimitNotificationStates.Single().GmailAttemptCount);
    }

    /// <summary>テスト送信失敗時にも本番配送状態を変更しないことを検証します。</summary>
    [TestMethod]
    public async Task SendAsync_Failure_DoesNotChangeProductionState()
    {
        ApplicationState state = CreateProductionState();
        string before = System.Text.Json.JsonSerializer.Serialize(state);
        RecordingApiClient api = new()
        {
            Exception = new GmailApiOperationException(
                GmailApiErrorKind.Transient,
                "一時的なエラーです。",
                new HttpRequestException()),
        };
        GmailTestMailSender sender = CreateSender(CreateAuthenticatedStatus(), api);

        GmailOperationResult result = await sender.SendAsync("target@example.com", CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(before, System.Text.Json.JsonSerializer.Serialize(state));
    }

    /// <summary>送信中の追加要求を拒否し、同時送信を1件に制限することを検証します。</summary>
    [TestMethod]
    public async Task SendAsync_ConcurrentCalls_AllowsOnlyOneRequest()
    {
        BlockingApiClient api = new();
        using GmailTestMailSender sender = CreateSender(CreateAuthenticatedStatus(), api);
        Task<GmailOperationResult> first = sender.SendAsync("one@example.com", CancellationToken.None);
        await api.Started.Task;

        GmailOperationResult second = await sender.SendAsync("two@example.com", CancellationToken.None);
        api.Release.TrySetResult();
        GmailOperationResult firstResult = await first;

        Assert.IsTrue(firstResult.Succeeded);
        Assert.IsFalse(second.Succeeded);
        StringAssert.Contains(second.Message, "送信中");
        Assert.AreEqual(1, api.SendCount);
    }

    /// <summary>テスト対象を生成します。</summary>
    private static GmailTestMailSender CreateSender(
        IGmailAuthenticationStatusProvider statusProvider,
        IGmailApiClient apiClient)
    {
        return new GmailTestMailSender(
            statusProvider,
            new GmailMimeMessageFactory(),
            apiClient,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 6, 3, 0, 0, TimeSpan.Zero)),
            NullLogger<GmailTestMailSender>.Instance);
    }

    /// <summary>認証済み状態を返すテストダブルを生成します。</summary>
    private static StubGmailAuthenticationService CreateAuthenticatedStatus()
    {
        return new StubGmailAuthenticationService
        {
            Status = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Authenticated,
                HasClientConfiguration = true,
                AuthenticatedEmailAddress = "sender@example.com",
            },
        };
    }

    /// <summary>本番通知チャネル状態と回復状態を含むテスト用状態を生成します。</summary>
    private static ApplicationState CreateProductionState()
    {
        return new ApplicationState
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
                    WindowsDeliveryStatus = DeliveryStatus.Succeeded,
                    GmailDeliveryStatus = DeliveryStatus.Failed,
                    GmailAttemptCount = 2,
                },
            ],
            RateLimitRecoveryStates =
            [
                new RateLimitRecoveryState
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = 300,
                    RecoverySequence = 4,
                },
            ],
        };
    }

    /// <summary>API呼び出しを記録するテスト用クライアントです。</summary>
    private class RecordingApiClient : IGmailApiClient
    {
        /// <summary>送信回数を取得します。</summary>
        public int SendCount { get; protected set; }

        /// <summary>最後のBase64URL MIMEを取得します。</summary>
        public string? LastRawMessage { get; private set; }

        /// <summary>送信時に発生させる例外を取得または設定します。</summary>
        public Exception? Exception { get; set; }

        /// <inheritdoc />
        public virtual Task SendRawMessageAsync(string base64UrlMimeMessage, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(base64UrlMimeMessage);
            cancellationToken.ThrowIfCancellationRequested();
            SendCount++;
            LastRawMessage = base64UrlMimeMessage;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>最初のAPI呼び出しをテストから解放するまで待機させます。</summary>
    private sealed class BlockingApiClient : RecordingApiClient
    {
        /// <summary>送信開始を通知します。</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>送信の完了を許可します。</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <inheritdoc />
        public override async Task SendRawMessageAsync(string base64UrlMimeMessage, CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(base64UrlMimeMessage);
            SendCount++;
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
        }
    }

    /// <summary>固定UTC時刻を返すテスト用時刻プロバイダーです。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        /// <summary>返却するUTC時刻を受け取ります。</summary>
        public FixedTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
