using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Application.Maintenance;

/// <summary>
/// 利用履歴とログの低頻度保守をsingle-flightで実行します。
/// </summary>
public sealed partial class ApplicationMaintenanceService : IApplicationMaintenanceService
{
    private static readonly TimeSpan MaintenanceInterval = TimeSpan.FromHours(24);
    private static readonly TimeSpan FailureRetryCheckInterval = TimeSpan.FromHours(1);
    private readonly ISettingsRepository settingsRepository;
    private readonly ApplicationStateStore stateStore;
    private readonly IUsageHistoryMaintenance usageHistoryMaintenance;
    private readonly ILogMaintenance logMaintenance;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ApplicationMaintenanceService> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly CancellationTokenSource shutdownCancellation = new();
    private readonly object startSync = new();
    private Task? backgroundTask;
    private bool disposed;

    /// <summary>
    /// 設定、状態、履歴・ログ保守、および時刻を受け取って初期化します。
    /// </summary>
    /// <param name="settingsRepository">保持日数の読み込み元です。</param>
    /// <param name="stateStore">最終保守時刻の永続化先です。</param>
    /// <param name="usageHistoryMaintenance">利用履歴の保守処理です。</param>
    /// <param name="logMaintenance">ログファイルの保守処理です。</param>
    /// <param name="timeProvider">現在時刻の提供元です。</param>
    /// <param name="logger">非致命エラーと結果の記録先です。</param>
    public ApplicationMaintenanceService(
        ISettingsRepository settingsRepository,
        ApplicationStateStore stateStore,
        IUsageHistoryMaintenance usageHistoryMaintenance,
        ILogMaintenance logMaintenance,
        TimeProvider timeProvider,
        ILogger<ApplicationMaintenanceService> logger)
    {
        ArgumentNullException.ThrowIfNull(settingsRepository);
        ArgumentNullException.ThrowIfNull(stateStore);
        ArgumentNullException.ThrowIfNull(usageHistoryMaintenance);
        ArgumentNullException.ThrowIfNull(logMaintenance);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.settingsRepository = settingsRepository;
        this.stateStore = stateStore;
        this.usageHistoryMaintenance = usageHistoryMaintenance;
        this.logMaintenance = logMaintenance;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public void Start()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        lock (startSync)
        {
            backgroundTask ??= Task.Run(
                () => RunScheduleAsync(shutdownCancellation.Token),
                CancellationToken.None);
        }
    }

    /// <inheritdoc />
    public async Task<bool> RunIfDueAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            DateTimeOffset nowUtc = timeProvider.GetUtcNow();
            ApplicationState state = await stateStore.LoadAsync(cancellationToken);
            if (state.LastMaintenanceAtUtc is DateTimeOffset lastMaintenanceAtUtc
                && nowUtc - lastMaintenanceAtUtc < MaintenanceInterval)
            {
                return false;
            }

            AppSettings settings = await settingsRepository.LoadAsync(cancellationToken);
            try
            {
                await usageHistoryMaintenance.MaintainAsync(
                    nowUtc.AddDays(-settings.HistoryRetentionDays),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogHistoryMaintenanceFailed(logger, exception);
            }

            try
            {
                await logMaintenance.MaintainAsync(
                    settings.LogRetentionDays,
                    timeProvider.GetLocalNow(),
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogLogMaintenanceFailed(logger, exception);
            }

            await stateStore.UpdateAsync(
                current => current with { LastMaintenanceAtUtc = nowUtc },
                cancellationToken);
            LogApplicationMaintenanceCompleted(logger, nowUtc, null);
            return true;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>起動直後に期限を確認し、その後は次の期限まで低頻度で待機します。</summary>
    private async Task RunScheduleAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunIfDueAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                LogApplicationMaintenanceFailed(logger, exception);
            }

            TimeSpan delay = await CalculateNextCheckDelayAsync(cancellationToken);
            try
            {
                await Task.Delay(delay, timeProvider, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>最終保守時刻から次に確認するまでの待機時間を計算します。</summary>
    private async Task<TimeSpan> CalculateNextCheckDelayAsync(CancellationToken cancellationToken)
    {
        try
        {
            ApplicationState state = await stateStore.LoadAsync(cancellationToken);
            if (state.LastMaintenanceAtUtc is not DateTimeOffset lastMaintenanceAtUtc)
            {
                return FailureRetryCheckInterval;
            }

            TimeSpan remaining = lastMaintenanceAtUtc + MaintenanceInterval - timeProvider.GetUtcNow();
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.FromMinutes(1);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogMaintenanceScheduleReadFailed(logger, exception);
            return FailureRetryCheckInterval;
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        await shutdownCancellation.CancelAsync();
        Task? runningTask;
        lock (startSync)
        {
            runningTask = backgroundTask;
        }

        if (runningTask is not null)
        {
            try
            {
                await runningTask;
            }
            catch (OperationCanceledException)
            {
                // 終了要求によるキャンセルは正常な停止として扱います。
            }
        }

        shutdownCancellation.Dispose();
        gate.Dispose();
    }

    [LoggerMessage(5300, LogLevel.Information, "運用保守が完了しました。MaintenanceAtUtc={MaintenanceAtUtc}")]
    private static partial void LogApplicationMaintenanceCompleted(
        ILogger logger,
        DateTimeOffset maintenanceAtUtc,
        Exception? exception);

    [LoggerMessage(5301, LogLevel.Warning, "利用履歴保守に失敗しました。利用枠監視は継続します。")]
    private static partial void LogHistoryMaintenanceFailed(ILogger logger, Exception exception);

    [LoggerMessage(5302, LogLevel.Warning, "ログ保守に失敗しました。利用枠監視は継続します。")]
    private static partial void LogLogMaintenanceFailed(ILogger logger, Exception exception);

    [LoggerMessage(5303, LogLevel.Warning, "運用保守に失敗しました。利用枠監視は継続します。")]
    private static partial void LogApplicationMaintenanceFailed(ILogger logger, Exception exception);

    [LoggerMessage(5304, LogLevel.Warning, "次回保守時刻を確認できないため1時間後に再確認します。")]
    private static partial void LogMaintenanceScheduleReadFailed(ILogger logger, Exception exception);
}
