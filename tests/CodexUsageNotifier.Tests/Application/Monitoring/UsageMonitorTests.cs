using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Monitoring;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Application.Monitoring;

/// <summary>
/// 利用枠取得要求の集約と再試行間隔を検証します。
/// </summary>
[TestClass]
public sealed class UsageMonitorTests
{
    /// <summary>
    /// 取得中に多数の追加要求が来ても、現在分に続く再取得1回へ集約することを検証します。
    /// </summary>
    [TestMethod]
    public async Task RequestRefreshAsync_CoalescesRequestsArrivingDuringFetch()
    {
        BlockingRateLimitClient client = new();
        InMemoryStateRepository repository = new();
        using ApplicationStateStore stateStore = new(repository);
        InMemorySettingsRepository settingsRepository = new();
        RecordingHistoryRepository historyRepository = new();
        RecordingStatusSink statusSink = new();
        await using UsageMonitor monitor = new(
            client,
            stateStore,
            settingsRepository,
            historyRepository,
            statusSink,
            TimeProvider.System,
            NullLogger<UsageMonitor>.Instance);

        Task first = monitor.RequestRefreshAsync(UsageCheckTrigger.Startup, CancellationToken.None);
        await client.FirstRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Task[] additional = Enumerable.Range(0, 20)
            .Select(_ => monitor.RequestRefreshAsync(UsageCheckTrigger.Manual, CancellationToken.None))
            .ToArray();
        client.ReleaseFirstRequest.TrySetResult();

        await Task.WhenAll(additional.Append(first));

        Assert.AreEqual(2, client.CallCount);
        Assert.AreEqual(2, statusSink.CheckingCount);
        Assert.AreEqual(2, statusSink.SnapshotCount);
        Assert.AreEqual(2, historyRepository.AppendCount);
    }

    /// <summary>
    /// 連続失敗回数に応じて1分、5分、15分へ延長することを検証します。
    /// </summary>
    [TestMethod]
    public void GetRetryDelay_ReturnsSpecifiedBackoff()
    {
        Assert.AreEqual(TimeSpan.FromMinutes(1), UsageMonitor.GetRetryDelay(1));
        Assert.AreEqual(TimeSpan.FromMinutes(5), UsageMonitor.GetRetryDelay(2));
        Assert.AreEqual(TimeSpan.FromMinutes(15), UsageMonitor.GetRetryDelay(3));
        Assert.AreEqual(TimeSpan.FromMinutes(15), UsageMonitor.GetRetryDelay(10));
    }

    /// <summary>
    /// 最初の要求だけを任意の時点まで停止できる利用枠クライアントです。
    /// </summary>
    private sealed class BlockingRateLimitClient : ICodexRateLimitClient
    {
        private int callCount;

        /// <summary>
        /// 利用枠更新通知です。
        /// </summary>
        public event EventHandler? RateLimitsUpdated;

        /// <summary>
        /// 接続切断通知です。
        /// </summary>
        public event EventHandler? ConnectionLost;

        /// <summary>
        /// ダミーのプロセスIDを取得します。
        /// </summary>
        public int? ProcessId => 123;

        /// <summary>
        /// 呼び出し回数を取得します。
        /// </summary>
        public int CallCount => Volatile.Read(ref callCount);

        /// <summary>
        /// 最初の要求が始まったことを通知します。
        /// </summary>
        public TaskCompletionSource FirstRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 最初の要求を完了可能にします。
        /// </summary>
        public TaskCompletionSource ReleaseFirstRequest { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// 利用枠を返し、最初の呼び出しだけテスト側の解除を待機します。
        /// </summary>
        /// <param name="trigger">取得契機です。</param>
        /// <param name="cancellationToken">処理のキャンセル通知です。</param>
        /// <returns>空の利用枠スナップショットです。</returns>
        public async Task<UsageSnapshot> ReadAsync(
            UsageCheckTrigger trigger,
            CancellationToken cancellationToken)
        {
            int currentCall = Interlocked.Increment(ref callCount);
            if (currentCall == 1)
            {
                FirstRequestStarted.TrySetResult();
                await ReleaseFirstRequest.Task.WaitAsync(cancellationToken);
            }

            return new UsageSnapshot
            {
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Trigger = trigger,
            };
        }

        /// <summary>
        /// コンパイラーがイベントを使用済みと認識できるようにします。
        /// </summary>
        public void TouchEvents()
        {
            _ = RateLimitsUpdated;
            _ = ConnectionLost;
        }
    }

    /// <summary>
    /// メモリ上だけで状態を保持するリポジトリです。
    /// </summary>
    private sealed class InMemoryStateRepository : IApplicationStateRepository
    {
        private ApplicationState state = ApplicationState.CreateDefault();

        /// <summary>
        /// 現在の状態を返します。
        /// </summary>
        /// <param name="cancellationToken">処理のキャンセル通知です。</param>
        /// <returns>現在の状態です。</returns>
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state);
        }

        /// <summary>
        /// 状態をメモリへ保存します。
        /// </summary>
        /// <param name="state">保存する状態です。</param>
        /// <param name="cancellationToken">処理のキャンセル通知です。</param>
        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(state);
            cancellationToken.ThrowIfCancellationRequested();
            this.state = state;
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 自動選択の初期設定を返すテスト用設定リポジトリです。
    /// </summary>
    private sealed class InMemorySettingsRepository : ISettingsRepository
    {
        /// <summary>
        /// 初期設定を返します。
        /// </summary>
        /// <param name="cancellationToken">処理のキャンセル通知です。</param>
        /// <returns>初期設定です。</returns>
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(AppSettings.CreateDefault());
        }

        /// <summary>
        /// このテストでは設定保存を行いません。
        /// </summary>
        /// <param name="settings">保存対象の設定です。</param>
        /// <param name="cancellationToken">処理のキャンセル通知です。</param>
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(settings);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// 履歴追記回数を記録するテスト用リポジトリです。
    /// </summary>
    private sealed class RecordingHistoryRepository : IUsageHistoryRepository
    {
        private int appendCount;

        /// <summary>
        /// 履歴追記回数を取得します。
        /// </summary>
        public int AppendCount => Volatile.Read(ref appendCount);

        /// <summary>
        /// 履歴追記を記録し、新規枠なしとして完了します。
        /// </summary>
        /// <param name="snapshot">保存対象のスナップショットです。</param>
        /// <param name="cancellationToken">処理のキャンセル通知です。</param>
        /// <returns>空の新規観測一覧です。</returns>
        public Task<IReadOnlyList<RateLimitObservation>> AppendAsync(
            UsageSnapshot snapshot,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref appendCount);
            return Task.FromResult<IReadOnlyList<RateLimitObservation>>(Array.Empty<RateLimitObservation>());
        }
    }

    /// <summary>
    /// 画面通知の回数だけを記録する出力先です。
    /// </summary>
    private sealed class RecordingStatusSink : IUsageStatusSink
    {
        private int checkingCount;
        private int snapshotCount;

        /// <summary>
        /// 取得開始通知の回数を取得します。
        /// </summary>
        public int CheckingCount => Volatile.Read(ref checkingCount);

        /// <summary>
        /// 正常取得通知の回数を取得します。
        /// </summary>
        public int SnapshotCount => Volatile.Read(ref snapshotCount);

        /// <summary>
        /// 取得開始を記録します。
        /// </summary>
        public void SetChecking() => Interlocked.Increment(ref checkingCount);

        /// <summary>
        /// 正常取得を記録します。
        /// </summary>
        /// <param name="snapshot">取得した利用枠です。</param>
        /// <param name="notificationTarget">選択された通知対象です。</param>
        public void SetSnapshot(UsageSnapshot snapshot, RateLimitWindow? notificationTarget)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            Interlocked.Increment(ref snapshotCount);
        }

        /// <summary>
        /// 失敗通知はこのテストでは使用しません。
        /// </summary>
        /// <param name="consecutiveFailures">連続失敗回数です。</param>
        /// <param name="message">エラー概要です。</param>
        public void SetFailure(int consecutiveFailures, string message)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(message);
        }
    }
}
