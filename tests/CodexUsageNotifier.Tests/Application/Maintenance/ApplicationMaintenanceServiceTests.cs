using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Maintenance;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Application.Maintenance;

/// <summary>
/// 起動時・日次保守の期限判定、排他、非致命エラー、および終了処理を検証します。
/// </summary>
[TestClass]
public sealed class ApplicationMaintenanceServiceTests
{
    private static readonly DateTimeOffset NowUtc =
        new(2026, 8, 11, 0, 0, 0, TimeSpan.Zero);

    /// <summary>最終保守時刻がない初回起動では履歴とログを保守することを検証します。</summary>
    [TestMethod]
    public async Task RunIfDueAsync_FirstRun_ExecutesBothMaintenanceTasks()
    {
        TestContext context = CreateContext(ApplicationState.CreateDefault());
        await using ApplicationMaintenanceService service = context.Service;

        bool executed = await service.RunIfDueAsync(CancellationToken.None);
        ApplicationState state = await context.StateStore.LoadAsync(CancellationToken.None);

        Assert.IsTrue(executed);
        Assert.AreEqual(1, context.History.CallCount);
        Assert.AreEqual(1, context.Log.CallCount);
        Assert.AreEqual(NowUtc.AddDays(-90), context.History.LastRetainedFromUtc);
        Assert.AreEqual(30, context.Log.LastRetentionDays);
        Assert.AreEqual(NowUtc, state.LastMaintenanceAtUtc);
    }

    /// <summary>前回保守から24時間未満では再実行しないことを検証します。</summary>
    [TestMethod]
    public async Task RunIfDueAsync_LessThanTwentyFourHours_SkipsMaintenance()
    {
        TestContext context = CreateContext(new ApplicationState
        {
            LastMaintenanceAtUtc = NowUtc.AddHours(-23).AddMinutes(-59),
        });
        await using ApplicationMaintenanceService service = context.Service;

        bool executed = await service.RunIfDueAsync(CancellationToken.None);

        Assert.IsFalse(executed);
        Assert.AreEqual(0, context.History.CallCount);
        Assert.AreEqual(0, context.Log.CallCount);
    }

    /// <summary>前回保守からちょうど24時間で再実行することを検証します。</summary>
    [TestMethod]
    public async Task RunIfDueAsync_TwentyFourHoursElapsed_ExecutesMaintenance()
    {
        TestContext context = CreateContext(new ApplicationState
        {
            LastMaintenanceAtUtc = NowUtc.AddHours(-24),
        });
        await using ApplicationMaintenanceService service = context.Service;

        bool executed = await service.RunIfDueAsync(CancellationToken.None);

        Assert.IsTrue(executed);
        Assert.AreEqual(1, context.History.CallCount);
        Assert.AreEqual(1, context.Log.CallCount);
    }

    /// <summary>重なった複数トリガーを直列化し、保守本体を1回だけ実行することを検証します。</summary>
    [TestMethod]
    public async Task RunIfDueAsync_ConcurrentTriggers_ExecutesSingleFlight()
    {
        TestContext context = CreateContext(ApplicationState.CreateDefault());
        context.History.BlockUntilReleased = true;
        await using ApplicationMaintenanceService service = context.Service;

        Task<bool> first = service.RunIfDueAsync(CancellationToken.None);
        await context.History.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Task<bool> second = service.RunIfDueAsync(CancellationToken.None);
        context.History.Release.TrySetResult();
        bool[] results = await Task.WhenAll(first, second);

        Assert.AreEqual(1, results.Count(value => value));
        Assert.AreEqual(1, context.History.CallCount);
        Assert.AreEqual(1, context.Log.CallCount);
    }

    /// <summary>履歴保守が失敗してもログ保守と最終試行時刻の保存を継続することを検証します。</summary>
    [TestMethod]
    public async Task RunIfDueAsync_HistoryFails_ContinuesLogMaintenance()
    {
        TestContext context = CreateContext(ApplicationState.CreateDefault());
        context.History.Exception = new IOException("history failure");
        await using ApplicationMaintenanceService service = context.Service;

        bool executed = await service.RunIfDueAsync(CancellationToken.None);
        ApplicationState state = await context.StateStore.LoadAsync(CancellationToken.None);

        Assert.IsTrue(executed);
        Assert.AreEqual(1, context.History.CallCount);
        Assert.AreEqual(1, context.Log.CallCount);
        Assert.AreEqual(NowUtc, state.LastMaintenanceAtUtc);
    }

    /// <summary>ログ保守が失敗しても例外を監視処理へ伝播しないことを検証します。</summary>
    [TestMethod]
    public async Task RunIfDueAsync_LogFails_RemainsNonFatal()
    {
        TestContext context = CreateContext(ApplicationState.CreateDefault());
        context.Log.Exception = new IOException("log failure");
        await using ApplicationMaintenanceService service = context.Service;

        bool executed = await service.RunIfDueAsync(CancellationToken.None);

        Assert.IsTrue(executed);
        Assert.AreEqual(1, context.History.CallCount);
        Assert.AreEqual(1, context.Log.CallCount);
    }

    /// <summary>終了時Cancellationで実行中のバックグラウンド保守を安全に停止することを検証します。</summary>
    [TestMethod]
    public async Task DisposeAsync_BackgroundMaintenance_CancelsRunningTask()
    {
        TestContext context = CreateContext(ApplicationState.CreateDefault());
        context.History.BlockUntilCanceled = true;
        ApplicationMaintenanceService service = context.Service;
        service.Start();
        await context.History.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        await service.DisposeAsync();

        Assert.IsTrue(context.History.CancellationObserved);
        Assert.AreEqual(0, context.Log.CallCount);
    }

    /// <summary>テスト対象と操作可能な依存関係を生成します。</summary>
    private static TestContext CreateContext(ApplicationState initialState)
    {
        InMemoryStateRepository stateRepository = new(initialState);
        ApplicationStateStore stateStore = new(stateRepository);
        RecordingHistoryMaintenance history = new();
        RecordingLogMaintenance log = new();
        ApplicationMaintenanceService service = new(
            new FixedSettingsRepository(AppSettings.CreateDefault()),
            stateStore,
            history,
            log,
            new FixedTimeProvider(NowUtc),
            NullLogger<ApplicationMaintenanceService>.Instance);
        return new TestContext(service, stateStore, history, log);
    }

    /// <summary>テスト対象と依存関係を保持します。</summary>
    private sealed record TestContext(
        ApplicationMaintenanceService Service,
        ApplicationStateStore StateStore,
        RecordingHistoryMaintenance History,
        RecordingLogMaintenance Log);

    /// <summary>固定設定を返すテスト用リポジトリです。</summary>
    private sealed class FixedSettingsRepository : ISettingsRepository
    {
        private readonly AppSettings settings;

        /// <summary>返す設定を指定して初期化します。</summary>
        public FixedSettingsRepository(AppSettings settings) => this.settings = settings;

        /// <inheritdoc />
        public Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(settings);
        }

        /// <inheritdoc />
        public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>状態をメモリへ保存するテスト用リポジトリです。</summary>
    private sealed class InMemoryStateRepository : IApplicationStateRepository
    {
        private ApplicationState state;

        /// <summary>初期状態を指定して初期化します。</summary>
        public InMemoryStateRepository(ApplicationState state) => this.state = state;

        /// <inheritdoc />
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(state);
        }

        /// <inheritdoc />
        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(state);
            cancellationToken.ThrowIfCancellationRequested();
            this.state = state;
            return Task.CompletedTask;
        }
    }

    /// <summary>履歴保守の呼び出しと制御状態を記録します。</summary>
    private sealed class RecordingHistoryMaintenance : IUsageHistoryMaintenance
    {
        /// <summary>保守呼び出し回数を取得します。</summary>
        public int CallCount { get; private set; }

        /// <summary>最後に指定された保持境界を取得します。</summary>
        public DateTimeOffset? LastRetainedFromUtc { get; private set; }

        /// <summary>保守時に発生させる例外を取得または設定します。</summary>
        public Exception? Exception { get; set; }

        /// <summary>テストが解放するまで待機するかどうかを取得または設定します。</summary>
        public bool BlockUntilReleased { get; set; }

        /// <summary>キャンセルされるまで待機するかどうかを取得または設定します。</summary>
        public bool BlockUntilCanceled { get; set; }

        /// <summary>保守開始を通知する完了元を取得します。</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>保守を解放する完了元を取得します。</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>キャンセルを観測したかどうかを取得します。</summary>
        public bool CancellationObserved { get; private set; }

        /// <inheritdoc />
        public async Task<UsageHistoryMaintenanceResult> MaintainAsync(
            DateTimeOffset retainedFromUtc,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRetainedFromUtc = retainedFromUtc;
            Started.TrySetResult();
            if (BlockUntilCanceled)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }

            if (BlockUntilReleased)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            if (Exception is not null)
            {
                throw Exception;
            }

            return new UsageHistoryMaintenanceResult();
        }
    }

    /// <summary>ログ保守の呼び出し内容を記録します。</summary>
    private sealed class RecordingLogMaintenance : ILogMaintenance
    {
        /// <summary>保守呼び出し回数を取得します。</summary>
        public int CallCount { get; private set; }

        /// <summary>最後に指定された保持日数を取得します。</summary>
        public int? LastRetentionDays { get; private set; }

        /// <summary>保守時に発生させる例外を取得または設定します。</summary>
        public Exception? Exception { get; set; }

        /// <inheritdoc />
        public Task<LogMaintenanceResult> MaintainAsync(
            int retentionDays,
            DateTimeOffset currentLocalTime,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRetentionDays = retentionDays;
            return Exception is null
                ? Task.FromResult(new LogMaintenanceResult())
                : Task.FromException<LogMaintenanceResult>(Exception);
        }
    }

    /// <summary>固定UTC時刻を返すテスト用時刻提供元です。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        /// <summary>返す時刻を指定して初期化します。</summary>
        public FixedTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
