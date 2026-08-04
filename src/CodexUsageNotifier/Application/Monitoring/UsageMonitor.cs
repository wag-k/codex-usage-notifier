using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Application.Monitoring;

/// <summary>
/// 利用枠取得を最大1件へ直列化し、取得中の追加要求を1件へ集約します。
/// </summary>
public sealed partial class UsageMonitor : IAsyncDisposable
{
    private readonly ICodexRateLimitClient client;
    private readonly ApplicationStateStore stateStore;
    private readonly ISettingsRepository settingsRepository;
    private readonly IUsageHistoryRepository historyRepository;
    private readonly IPowerEventSource powerEventSource;
    private readonly RateLimitNotificationProcessor notificationProcessor;
    private readonly IUsageStatusSink statusSink;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<UsageMonitor> logger;
    private readonly object syncRoot = new();
    private readonly SemaphoreSlim executionGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private Task? workerTask;
    private Task? debounceTask;
    private Task? retryTask;
    private Task? periodicTask;
    private Task? resetTask;
    private Task? quietHoursTask;
    private CancellationTokenSource? debounceCancellation;
    private CancellationTokenSource? retryCancellation;
    private CancellationTokenSource? resetCancellation;
    private CancellationTokenSource? quietHoursCancellation;
    private DateTimeOffset? periodicNextCheckUtc;
    private DateTimeOffset? resetNextCheckUtc;
    private DateTimeOffset? quietHoursNextCheckUtc;
    private DateTimeOffset? retryNextCheckUtc;
    private bool refreshPending;
    private bool fetchInProgress;
    private bool disposed;
    private UsageCheckTrigger pendingTrigger = UsageCheckTrigger.Unknown;

    /// <summary>
    /// 利用枠クライアント、状態保存、表示先、時刻、およびロガーを受け取って初期化します。
    /// </summary>
    /// <param name="client">Codex App Serverから利用枠を取得するクライアントです。</param>
    /// <param name="stateStore">取得結果と失敗回数を保存する状態ストアです。</param>
    /// <param name="settingsRepository">通知対象選択設定の読み込み元です。</param>
    /// <param name="historyRepository">全利用枠の観測履歴保存先です。</param>
    /// <param name="powerEventSource">スリープ復帰を通知するイベント元です。</param>
    /// <param name="notificationProcessor">通知判定・送信・状態保存を調整する処理です。</param>
    /// <param name="statusSink">監視状態の表示先です。</param>
    /// <param name="timeProvider">デバウンスと再試行に使用する時刻提供元です。</param>
    /// <param name="logger">監視結果を記録するロガーです。</param>
    public UsageMonitor(
        ICodexRateLimitClient client,
        ApplicationStateStore stateStore,
        ISettingsRepository settingsRepository,
        IUsageHistoryRepository historyRepository,
        IPowerEventSource powerEventSource,
        RateLimitNotificationProcessor notificationProcessor,
        IUsageStatusSink statusSink,
        TimeProvider timeProvider,
        ILogger<UsageMonitor> logger)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(settingsRepository);
        ArgumentNullException.ThrowIfNull(historyRepository);
        ArgumentNullException.ThrowIfNull(powerEventSource);
        ArgumentNullException.ThrowIfNull(notificationProcessor);
        ArgumentNullException.ThrowIfNull(statusSink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.client = client;
        this.stateStore = stateStore;
        this.settingsRepository = settingsRepository;
        this.historyRepository = historyRepository;
        this.powerEventSource = powerEventSource;
        this.notificationProcessor = notificationProcessor;
        this.statusSink = statusSink;
        this.timeProvider = timeProvider;
        this.logger = logger;
        client.RateLimitsUpdated += OnRateLimitsUpdated;
        client.ConnectionLost += OnConnectionLost;
        powerEventSource.Resumed += OnResumed;
    }

    /// <summary>
    /// アプリ起動時の利用枠取得をバックグラウンドで予約します。
    /// </summary>
    public void Start()
    {
        lock (syncRoot)
        {
            if (periodicTask is null)
            {
                periodicTask = Task.Run(PeriodicRefreshLoopAsync, CancellationToken.None);
            }
        }

        _ = RequestRefreshAsync(UsageCheckTrigger.Startup, CancellationToken.None);
    }

    /// <summary>
    /// 利用枠の再取得を要求し、同時要求を現在分と追加1件へ集約します。
    /// </summary>
    /// <param name="trigger">利用枠を取得する契機です。</param>
    /// <param name="cancellationToken">呼び出し側の待機キャンセル通知です。</param>
    /// <returns>現在予約されている取得処理の完了を表します。</returns>
    public Task RequestRefreshAsync(UsageCheckTrigger trigger, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Task activeWorker;
        lock (syncRoot)
        {
            refreshPending = true;
            pendingTrigger = trigger;
            if (workerTask is null || workerTask.IsCompleted)
            {
                workerTask = Task.Run(ProcessQueueAsync, CancellationToken.None);
            }

            activeWorker = workerTask;
        }

        return activeWorker.WaitAsync(cancellationToken);
    }

    /// <summary>
    /// 集約された要求を1件ずつ取り出して利用枠を取得します。
    /// </summary>
    private async Task ProcessQueueAsync()
    {
        while (!lifetimeCancellation.IsCancellationRequested)
        {
            UsageCheckTrigger trigger;
            lock (syncRoot)
            {
                if (!refreshPending)
                {
                    return;
                }

                refreshPending = false;
                trigger = pendingTrigger;
                fetchInProgress = true;
            }

            await executionGate.WaitAsync(lifetimeCancellation.Token);
            try
            {
                await FetchOnceAsync(trigger, lifetimeCancellation.Token);
            }
            catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }
            finally
            {
                executionGate.Release();
                lock (syncRoot)
                {
                    fetchInProgress = false;
                }
            }
        }
    }

    /// <summary>
    /// 利用枠を1回取得し、成功結果または失敗回数を保存・表示します。
    /// </summary>
    /// <param name="trigger">利用枠を取得する契機です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    private async Task FetchOnceAsync(UsageCheckTrigger trigger, CancellationToken cancellationToken)
    {
        statusSink.SetChecking();
        try
        {
            UsageSnapshot snapshot = await client.ReadAsync(trigger, cancellationToken);
            IReadOnlyList<RateLimitObservation> newlyObserved = await historyRepository.AppendAsync(
                snapshot,
                cancellationToken);
            foreach (RateLimitObservation observation in newlyObserved)
            {
                LogNewRateLimitObserved(
                    logger,
                    observation.LimitId ?? "(null)",
                    observation.Position,
                    observation.WindowDurationMinutes,
                    observation.Classification);
            }

            AppSettings settings = await settingsRepository.LoadAsync(cancellationToken);
            IReadOnlyList<RateLimitWindow> selectableRateLimits = settings.IncludeUnknownRateLimitsInNotifications
                ? snapshot.RateLimits
                : snapshot.RateLimits
                    .Where(window => window.Classification != RateLimitClassification.Unknown)
                    .ToArray();
            RateLimitWindow? notificationTarget = NotificationTargetSelector.Select(
                selectableRateLimits,
                settings.NotificationTarget);
            NotificationProcessingResult processingResult = await notificationProcessor.ProcessAsync(
                snapshot,
                notificationTarget,
                settings,
                cancellationToken);
            CancelRetry();
            ScheduleResetCheck(snapshot, settings);
            if (processingResult.DeferredUntilUtc is not null)
            {
                ScheduleQuietHoursCheck(processingResult.DeferredUntilUtc.Value);
            }

            statusSink.SetSnapshot(
                snapshot,
                notificationTarget,
                processingResult.State);
            int unknownCount = snapshot.RateLimits.Count(
                window => window.Classification == RateLimitClassification.Unknown);
            LogFetchSucceeded(logger, client.ProcessId, snapshot.RateLimits.Count, unknownCount);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
        {
            await RecordFailureAndScheduleRetryAsync(exception, cancellationToken);
        }
    }

    /// <summary>
    /// 取得失敗を永続化して表示し、仕様の段階的な間隔で再試行を1件だけ予約します。
    /// </summary>
    /// <param name="exception">取得時に発生した例外です。</param>
    /// <param name="cancellationToken">状態保存のキャンセル通知です。</param>
    private async Task RecordFailureAndScheduleRetryAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(exception);
        ApplicationState state = await stateStore.UpdateAsync(
            current => current with { ConsecutiveFailures = current.ConsecutiveFailures + 1 },
            cancellationToken);
        AppSettings settings = await settingsRepository.LoadAsync(cancellationToken);
        state = await notificationProcessor.NotifyMonitoringFailureAsync(
            state,
            settings,
            cancellationToken);
        string summary = CreateSafeErrorSummary(exception);
        statusSink.SetFailure(state.ConsecutiveFailures, summary);
        LogFetchFailed(logger, state.ConsecutiveFailures, exception);
        ScheduleRetry(GetRetryDelay(state.ConsecutiveFailures));
    }

    /// <summary>
    /// 利用枠更新通知を受け、通知本文を状態として採用せず1秒後の再取得を予約します。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">イベント引数です。</param>
    private void OnRateLimitsUpdated(object? sender, EventArgs e)
    {
        lock (syncRoot)
        {
            debounceCancellation?.Cancel();
            debounceCancellation?.Dispose();
            debounceCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
            debounceTask = Task.Run(
                () => DebounceRefreshAsync(debounceCancellation.Token),
                CancellationToken.None);
        }
    }

    /// <summary>
    /// 更新通知の連続受信をデバウンスして利用枠を再取得します。
    /// </summary>
    /// <param name="cancellationToken">デバウンスのキャンセル通知です。</param>
    private async Task DebounceRefreshAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(1), timeProvider, cancellationToken);
            await RequestRefreshAsync(UsageCheckTrigger.Scheduled, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 設定された補助確認間隔で利用枠取得を継続します。
    /// </summary>
    private async Task PeriodicRefreshLoopAsync()
    {
        try
        {
            while (!lifetimeCancellation.IsCancellationRequested)
            {
                AppSettings settings = await settingsRepository.LoadAsync(lifetimeCancellation.Token);
                TimeSpan delay = TimeSpan.FromMinutes(settings.FallbackPollingMinutes);
                lock (syncRoot)
                {
                    periodicNextCheckUtc = timeProvider.GetUtcNow().Add(delay);
                }

                UpdateNextCheckDisplay();
                await Task.Delay(delay, timeProvider, lifetimeCancellation.Token);
                lock (syncRoot)
                {
                    periodicNextCheckUtc = null;
                }

                UpdateNextCheckDisplay();
                await RequestRefreshAsync(UsageCheckTrigger.Scheduled, lifetimeCancellation.Token);
            }
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// スリープ復帰時に即時取得をバックグラウンドへ予約します。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">イベント引数です。</param>
    private void OnResumed(object? sender, EventArgs e)
    {
        if (disposed)
        {
            return;
        }

        _ = Task.Run(HandleResumeAsync, CancellationToken.None);
    }

    /// <summary>
    /// スリープ復帰時の取得を実行し、アプリ終了との競合によるキャンセルを正常に処理します。
    /// </summary>
    private async Task HandleResumeAsync()
    {
        try
        {
            await RequestRefreshAsync(UsageCheckTrigger.Resume, lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (disposed)
        {
        }
    }

    /// <summary>
    /// 取得できた全利用枠のうち最も早いリセット直後の再取得を予約します。
    /// </summary>
    /// <param name="snapshot">現在取得した全利用枠です。</param>
    /// <param name="settings">リセット後の待機時間設定です。</param>
    private void ScheduleResetCheck(UsageSnapshot snapshot, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        DateTimeOffset? nextResetCheck = GetNextResetCheckAtUtc(
            snapshot,
            settings,
            timeProvider.GetUtcNow());

        lock (syncRoot)
        {
            resetCancellation?.Cancel();
            resetCancellation?.Dispose();
            resetCancellation = null;
            resetTask = null;
            resetNextCheckUtc = nextResetCheck;
            if (nextResetCheck is not null)
            {
                resetCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
                CancellationTokenSource scheduledCancellation = resetCancellation;
                resetTask = Task.Run(
                    () => RefreshAtAsync(
                        nextResetCheck.Value,
                        UsageCheckTrigger.Scheduled,
                        scheduledCancellation,
                        ScheduledCheckKind.Reset),
                    CancellationToken.None);
            }
        }

        UpdateNextCheckDisplay();
    }

    /// <summary>
    /// 全利用枠から現在より後にある最も早いリセット後確認時刻を返します。
    /// </summary>
    /// <param name="snapshot">現在取得した全利用枠です。</param>
    /// <param name="settings">リセット後の待機時間設定です。</param>
    /// <param name="nowUtc">比較基準となる現在UTC時刻です。</param>
    /// <returns>次回確認UTC時刻です。将来のリセットがなければnullです。</returns>
    internal static DateTimeOffset? GetNextResetCheckAtUtc(
        UsageSnapshot snapshot,
        AppSettings settings,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        return snapshot.RateLimits
            .Where(window => window.ResetsAtUtc is not null)
            .Select(window => window.ResetsAtUtc!.Value.AddSeconds(settings.ResetCheckDelaySeconds))
            .Where(value => value > nowUtc)
            .OrderBy(value => value)
            .Select(value => (DateTimeOffset?)value)
            .FirstOrDefault();
    }

    /// <summary>
    /// 通知禁止時間の終了直後に再取得を予約します。
    /// </summary>
    /// <param name="deferredUntilUtc">通知禁止時間が終了するUTC時刻です。</param>
    private void ScheduleQuietHoursCheck(DateTimeOffset deferredUntilUtc)
    {
        lock (syncRoot)
        {
            if (quietHoursNextCheckUtc is not null
                && quietHoursNextCheckUtc.Value <= deferredUntilUtc
                && quietHoursTask is { IsCompleted: false })
            {
                return;
            }

            quietHoursCancellation?.Cancel();
            quietHoursCancellation?.Dispose();
            quietHoursCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
            CancellationTokenSource scheduledCancellation = quietHoursCancellation;
            quietHoursNextCheckUtc = deferredUntilUtc;
            quietHoursTask = Task.Run(
                () => RefreshAtAsync(
                    deferredUntilUtc,
                    UsageCheckTrigger.Scheduled,
                    scheduledCancellation,
                    ScheduledCheckKind.QuietHours),
                CancellationToken.None);
        }

        UpdateNextCheckDisplay();
    }

    /// <summary>
    /// 指定UTC時刻まで待機し、リセットまたは通知禁止時間終了の再取得を行います。
    /// </summary>
    /// <param name="scheduledAtUtc">再取得予定UTC時刻です。</param>
    /// <param name="trigger">利用枠取得契機です。</param>
    /// <param name="scheduledCancellation">予約専用のキャンセル元です。</param>
    /// <param name="kind">予約の種類です。</param>
    private async Task RefreshAtAsync(
        DateTimeOffset scheduledAtUtc,
        UsageCheckTrigger trigger,
        CancellationTokenSource scheduledCancellation,
        ScheduledCheckKind kind)
    {
        ArgumentNullException.ThrowIfNull(scheduledCancellation);
        try
        {
            TimeSpan delay = scheduledAtUtc - timeProvider.GetUtcNow();
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, timeProvider, scheduledCancellation.Token);
            }

            lock (syncRoot)
            {
                if (kind == ScheduledCheckKind.Reset
                    && ReferenceEquals(resetCancellation, scheduledCancellation))
                {
                    resetNextCheckUtc = null;
                    resetTask = null;
                    resetCancellation = null;
                }
                else if (kind == ScheduledCheckKind.QuietHours
                         && ReferenceEquals(quietHoursCancellation, scheduledCancellation))
                {
                    quietHoursNextCheckUtc = null;
                    quietHoursTask = null;
                    quietHoursCancellation = null;
                }
            }

            UpdateNextCheckDisplay();
            scheduledCancellation.Dispose();
            await RequestRefreshAsync(trigger, lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (
            scheduledCancellation.IsCancellationRequested
                || lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 待機中にApp Serverが終了した場合、取得中でなければ障害として記録して再試行します。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">イベント引数です。</param>
    private void OnConnectionLost(object? sender, EventArgs e)
    {
        lock (syncRoot)
        {
            if (fetchInProgress || disposed)
            {
                return;
            }
        }

        _ = Task.Run(HandleConnectionLostAsync, CancellationToken.None);
    }

    /// <summary>
    /// 待機中の接続切断を障害として記録し、終了中のキャンセルは正常に処理します。
    /// </summary>
    private async Task HandleConnectionLostAsync()
    {
        try
        {
            await RecordFailureAndScheduleRetryAsync(
                new EndOfStreamException("Codex App Serverが予期せず終了しました。"),
                lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 指定時間後の再取得を最大1件だけ予約します。
    /// </summary>
    /// <param name="delay">再取得までの待機時間です。</param>
    private void ScheduleRetry(TimeSpan delay)
    {
        lock (syncRoot)
        {
            if (retryTask is not null && !retryTask.IsCompleted)
            {
                return;
            }

            retryCancellation?.Dispose();
            retryCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeCancellation.Token);
            CancellationTokenSource scheduledCancellation = retryCancellation;
            retryNextCheckUtc = timeProvider.GetUtcNow().Add(delay);
            retryTask = Task.Run(
                () => RetryAfterDelayAsync(delay, scheduledCancellation),
                CancellationToken.None);
        }

        UpdateNextCheckDisplay();
    }

    /// <summary>
    /// 再試行間隔の経過後に取得を予約します。
    /// </summary>
    /// <param name="delay">再取得までの待機時間です。</param>
    /// <param name="scheduledCancellation">この再試行予約のキャンセル元です。</param>
    private async Task RetryAfterDelayAsync(TimeSpan delay, CancellationTokenSource scheduledCancellation)
    {
        ArgumentNullException.ThrowIfNull(scheduledCancellation);
        try
        {
            await Task.Delay(delay, timeProvider, scheduledCancellation.Token);
            lock (syncRoot)
            {
                if (ReferenceEquals(retryCancellation, scheduledCancellation))
                {
                    retryTask = null;
                    retryCancellation = null;
                    retryNextCheckUtc = null;
                }
            }

            UpdateNextCheckDisplay();
            scheduledCancellation.Dispose();
            await RequestRefreshAsync(UsageCheckTrigger.Scheduled, lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (
            scheduledCancellation.IsCancellationRequested
                || lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// 成功時に予約済みの再試行を無効化します。
    /// </summary>
    private void CancelRetry()
    {
        lock (syncRoot)
        {
            retryCancellation?.Cancel();
            retryCancellation?.Dispose();
            retryCancellation = null;
            retryTask = null;
            retryNextCheckUtc = null;
        }

        UpdateNextCheckDisplay();
    }

    /// <summary>
    /// 現在予約されている確認時刻のうち最も早いものを状態画面へ通知します。
    /// </summary>
    private void UpdateNextCheckDisplay()
    {
        DateTimeOffset? nextCheck;
        lock (syncRoot)
        {
            nextCheck = new[]
                {
                    periodicNextCheckUtc,
                    resetNextCheckUtc,
                    quietHoursNextCheckUtc,
                    retryNextCheckUtc,
                }
                .Where(value => value is not null)
                .Min();
        }

        statusSink.SetNextCheck(nextCheck);
    }

    /// <summary>
    /// 連続失敗回数に対応する再試行間隔を返します。
    /// </summary>
    /// <param name="consecutiveFailures">連続失敗回数です。</param>
    /// <returns>1回目は1分、2回目は5分、3回目以降は15分です。</returns>
    internal static TimeSpan GetRetryDelay(int consecutiveFailures) => consecutiveFailures switch
    {
        <= 1 => TimeSpan.FromMinutes(1),
        2 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(15),
    };

    /// <summary>
    /// 画面表示用に機密情報を含まないエラー概要を生成します。
    /// </summary>
    /// <param name="exception">取得失敗の例外です。</param>
    /// <returns>利用者向けの短い概要です。</returns>
    private static string CreateSafeErrorSummary(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            TimeoutException => "Codex App Serverが時間内に応答しませんでした。",
            InvalidDataException => "利用枠レスポンスを解釈できませんでした。",
            _ => "Codex App Serverへ接続できませんでした。",
        };
    }

    /// <summary>
    /// イベント購読、待機処理、および同期資源を解放します。
    /// </summary>
    /// <returns>解放完了を表す非同期処理です。</returns>
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        client.RateLimitsUpdated -= OnRateLimitsUpdated;
        client.ConnectionLost -= OnConnectionLost;
        powerEventSource.Resumed -= OnResumed;
        lifetimeCancellation.Cancel();
        debounceCancellation?.Cancel();
        retryCancellation?.Cancel();
        resetCancellation?.Cancel();
        quietHoursCancellation?.Cancel();

        Task?[] tasks;
        lock (syncRoot)
        {
            tasks = [workerTask, debounceTask, retryTask, periodicTask, resetTask, quietHoursTask];
        }

        foreach (Task? task in tasks)
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        debounceCancellation?.Dispose();
        retryCancellation?.Dispose();
        resetCancellation?.Dispose();
        quietHoursCancellation?.Dispose();
        lifetimeCancellation.Dispose();
        executionGate.Dispose();
    }

    [LoggerMessage(2200, LogLevel.Information, "利用枠を取得しました。ProcessId={ProcessId}, RateLimitCount={RateLimitCount}, UnknownWindowCount={UnknownWindowCount}")]
    private static partial void LogFetchSucceeded(
        ILogger logger,
        int? processId,
        int rateLimitCount,
        int unknownWindowCount);

    [LoggerMessage(2201, LogLevel.Warning, "利用枠の取得に失敗しました。ConsecutiveFailures={ConsecutiveFailures}")]
    private static partial void LogFetchFailed(ILogger logger, int consecutiveFailures, Exception exception);

    [LoggerMessage(2202, LogLevel.Information, "新しい利用枠を初めて観測しました。LimitId={LimitId}, Position={Position}, WindowDurationMins={WindowDurationMins}, Classification={Classification}")]
    private static partial void LogNewRateLimitObserved(
        ILogger logger,
        string limitId,
        RateLimitPosition position,
        int? windowDurationMins,
        RateLimitClassification classification);

    /// <summary>
    /// 専用タイマーで予約する確認の種類を表します。
    /// </summary>
    private enum ScheduledCheckKind
    {
        /// <summary>
        /// 利用枠のリセット直後確認を表します。
        /// </summary>
        Reset,

        /// <summary>
        /// 通知禁止時間終了後の確認を表します。
        /// </summary>
        QuietHours,
    }
}
