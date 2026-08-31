using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation.Trends;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Presentation.ViewModels;

/// <summary>
/// 既存履歴の非同期読み込み、期間選択、および表示用使用率系列を管理します。
/// </summary>
public sealed partial class UsageTrendViewModel : INotifyPropertyChanged, IDisposable
{
    private const int MaximumDisplayedPointsPerSeries = 1500;
    private readonly IUsageHistoryReader? historyReader;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<UsageTrendViewModel> logger;
    private readonly SemaphoreSlim loadingGate = new(1, 1);
    private UsageTrendHistory allHistory = new();
    private IReadOnlyList<UsageTrendSeriesPoint> fiveHourPoints = Array.Empty<UsageTrendSeriesPoint>();
    private IReadOnlyList<UsageTrendSeriesPoint> weeklyPoints = Array.Empty<UsageTrendSeriesPoint>();
    private UsageTrendRange selectedRange = UsageTrendRange.Days7;
    private DateTimeOffset rangeStartUtc;
    private DateTimeOffset rangeEndUtc;
    private TimeSpan gapThreshold = TimeSpan.FromHours(2);
    private bool isLoading;
    private bool isLoaded;
    private bool hasVisiblePoints;
    private string statusMessage = "履歴を読み込んでいます…";
    private bool disposed;

    /// <summary>
    /// 履歴Reader、現在時刻、および安全な診断ロガーを受け取って初期化します。
    /// </summary>
    /// <param name="historyReader">既存JSONL履歴の読み取り元です。</param>
    /// <param name="timeProvider">表示期間の終端を提供します。</param>
    /// <param name="logger">読み込み失敗を記録するロガーです。</param>
    public UsageTrendViewModel(
        IUsageHistoryReader historyReader,
        TimeProvider timeProvider,
        ILogger<UsageTrendViewModel> logger)
    {
        ArgumentNullException.ThrowIfNull(historyReader);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.historyReader = historyReader;
        this.timeProvider = timeProvider;
        this.logger = logger;
        RangeOptions = CreateRangeOptions();
        RefreshVisiblePoints();
    }

    /// <summary>外部ファイルを読み取らない表示テスト用インスタンスを初期化します。</summary>
    internal UsageTrendViewModel()
    {
        timeProvider = TimeProvider.System;
        logger = NullLogger<UsageTrendViewModel>.Instance;
        RangeOptions = CreateRangeOptions();
        RefreshVisiblePoints();
    }

    /// <summary>表示値が変更されたときに発生します。</summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>期間切替へ表示する選択肢を取得します。</summary>
    public IReadOnlyList<UsageTrendRangeOptionViewModel> RangeOptions { get; }

    /// <summary>現在選択している表示期間を取得または設定します。</summary>
    public UsageTrendRange SelectedRange
    {
        get => selectedRange;
        set
        {
            if (selectedRange == value)
            {
                return;
            }

            selectedRange = value;
            OnPropertyChanged();
            RefreshVisiblePoints();
        }
    }

    /// <summary>表示対象の5時間枠系列を取得します。</summary>
    public IReadOnlyList<UsageTrendSeriesPoint> FiveHourPoints
    {
        get => fiveHourPoints;
        private set => SetProperty(ref fiveHourPoints, value);
    }

    /// <summary>表示対象の週間枠系列を取得します。</summary>
    public IReadOnlyList<UsageTrendSeriesPoint> WeeklyPoints
    {
        get => weeklyPoints;
        private set => SetProperty(ref weeklyPoints, value);
    }

    /// <summary>X軸の開始UTC時刻を取得します。</summary>
    public DateTimeOffset RangeStartUtc
    {
        get => rangeStartUtc;
        private set => SetProperty(ref rangeStartUtc, value);
    }

    /// <summary>X軸の終了UTC時刻を取得します。</summary>
    public DateTimeOffset RangeEndUtc
    {
        get => rangeEndUtc;
        private set => SetProperty(ref rangeEndUtc, value);
    }

    /// <summary>線を分割する未観測時間の閾値を取得します。</summary>
    public TimeSpan GapThreshold
    {
        get => gapThreshold;
        private set => SetProperty(ref gapThreshold, value);
    }

    /// <summary>履歴を読み込み中かどうかを取得します。</summary>
    public bool IsLoading
    {
        get => isLoading;
        private set => SetProperty(ref isLoading, value);
    }

    /// <summary>選択期間に表示できる取得点があるかどうかを取得します。</summary>
    public bool HasVisiblePoints
    {
        get => hasVisiblePoints;
        private set => SetProperty(ref hasVisiblePoints, value);
    }

    /// <summary>読み込み中、空期間、または失敗時の説明を取得します。</summary>
    public string StatusMessage
    {
        get => statusMessage;
        private set => SetProperty(ref statusMessage, value);
    }

    /// <summary>
    /// 初回だけ現在時刻から90日前までの既存履歴を非同期に読み込みます。
    /// </summary>
    /// <param name="cancellationToken">画面終了時のキャンセル通知です。</param>
    /// <returns>読み込みと表示更新の完了を表す処理です。</returns>
    public async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (isLoaded || historyReader is null)
        {
            return;
        }

        await loadingGate.WaitAsync(cancellationToken);
        try
        {
            if (isLoaded)
            {
                return;
            }

            IsLoading = true;
            StatusMessage = "履歴を読み込んでいます…";
            DateTimeOffset nowUtc = timeProvider.GetUtcNow();
            IReadOnlyList<UsageHistoryEntry> entries = await historyReader.ReadAsync(
                nowUtc - UsageTrendRangeDefinition.GetDuration(UsageTrendRange.Days90),
                nowUtc,
                cancellationToken);
            UsageTrendHistory loaded = UsageTrendMapper.Map(entries, TimeZoneInfo.Local);
            allHistory = MergeHistories(loaded, allHistory);
            isLoaded = true;
            IsLoading = false;
            RefreshVisiblePoints();
            LogUsageTrendHistoryLoaded(logger, entries.Count, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            StatusMessage = "履歴を読み込めませんでした";
            LogUsageTrendHistoryLoadFailed(logger, exception);
        }
        finally
        {
            IsLoading = false;
            loadingGate.Release();
        }
    }

    /// <summary>
    /// 正常取得した最新スナップショットをメモリ上の系列へ即時反映します。
    /// </summary>
    /// <param name="snapshot">正常取得した全利用枠です。</param>
    public void AddSnapshot(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        UsageHistoryEntry entry = new()
        {
            CapturedAtUtc = snapshot.CapturedAtUtc,
            RateLimits = snapshot.RateLimits.Select(window => new RateLimitObservation
            {
                LimitId = window.LimitId,
                Position = window.Position,
                WindowDurationMinutes = window.WindowDurationMinutes,
                UsedPercent = window.UsedPercent,
                ResetsAtUtc = window.ResetsAtUtc,
                Classification = window.Classification,
            }).ToArray(),
        };
        UsageTrendHistory latest = UsageTrendMapper.Map([entry], TimeZoneInfo.Local);
        allHistory = MergeHistories(allHistory, latest);
        RefreshVisiblePoints();
    }

    /// <summary>
    /// 補助確認間隔から長時間gapの判定閾値を更新します。
    /// </summary>
    /// <param name="fallbackPollingMinutes">補助確認間隔の分数です。</param>
    public void UpdatePollingInterval(int fallbackPollingMinutes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fallbackPollingMinutes);
        GapThreshold = TimeSpan.FromMinutes(fallbackPollingMinutes * 2D);
    }

    /// <summary>
    /// 選択期間、表示点、および空状態メッセージを現在時刻で再計算します。
    /// </summary>
    private void RefreshVisiblePoints()
    {
        DateTimeOffset nowUtc = timeProvider.GetUtcNow();
        RangeEndUtc = nowUtc;
        RangeStartUtc = nowUtc - UsageTrendRangeDefinition.GetDuration(selectedRange);
        UsageTrendHistory filtered = UsageTrendMapper.Filter(allHistory, selectedRange, nowUtc);
        FiveHourPoints = UsageTrendDownsampler.Downsample(
            filtered.FiveHourPoints,
            MaximumDisplayedPointsPerSeries);
        WeeklyPoints = UsageTrendDownsampler.Downsample(
            filtered.WeeklyPoints,
            MaximumDisplayedPointsPerSeries);
        HasVisiblePoints = FiveHourPoints.Count > 0 || WeeklyPoints.Count > 0;
        if (!IsLoading)
        {
            StatusMessage = HasVisiblePoints
                ? string.Empty
                : allHistory.HasAnyPoints
                    ? "この期間の使用履歴はありません。"
                    : "使用履歴はまだありません。\n利用枠を取得すると、ここに推移が表示されます。";
        }
    }

    /// <summary>
    /// 2つの履歴を分類ごとに時刻重複を除いて結合します。
    /// </summary>
    private static UsageTrendHistory MergeHistories(
        UsageTrendHistory first,
        UsageTrendHistory second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        return new UsageTrendHistory
        {
            FiveHourPoints = MergePoints(first.FiveHourPoints, second.FiveHourPoints),
            WeeklyPoints = MergePoints(first.WeeklyPoints, second.WeeklyPoints),
        };
    }

    /// <summary>系列点を取得時刻で重複排除して昇順へ並べます。</summary>
    private static UsageTrendSeriesPoint[] MergePoints(
        IReadOnlyList<UsageTrendSeriesPoint> first,
        IReadOnlyList<UsageTrendSeriesPoint> second)
    {
        return first.Concat(second)
            .OrderBy(point => point.CapturedAtUtc)
            .DistinctBy(point => point.CapturedAtUtc)
            .ToArray();
    }

    /// <summary>期間切替用の固定選択肢を生成します。</summary>
    private static UsageTrendRangeOptionViewModel[] CreateRangeOptions()
    {
        return Enum.GetValues<UsageTrendRange>()
            .Select(range => new UsageTrendRangeOptionViewModel
            {
                Range = range,
                DisplayName = UsageTrendRangeDefinition.GetDisplayName(range),
            })
            .ToArray();
    }

    /// <summary>プロパティ値を変更し、差がある場合だけ通知します。</summary>
    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        OnPropertyChanged(propertyName);
    }

    /// <summary>指定プロパティの変更を画面へ通知します。</summary>
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>履歴読み込みの同期資源を解放します。</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        loadingGate.Dispose();
    }

    /// <summary>使用率履歴の読み込み成功を記録します。</summary>
    [LoggerMessage(6000, LogLevel.Information, "使用率推移用の履歴を読み込みました。EntryCount={EntryCount}")]
    private static partial void LogUsageTrendHistoryLoaded(
        ILogger logger,
        int entryCount,
        Exception? exception);

    /// <summary>使用率履歴の読み込み失敗を記録します。</summary>
    [LoggerMessage(6001, LogLevel.Warning, "使用率推移用の履歴を読み込めませんでした。")]
    private static partial void LogUsageTrendHistoryLoadFailed(ILogger logger, Exception exception);
}

/// <summary>
/// 期間切替リストに表示する値と表示名を保持します。
/// </summary>
public sealed record UsageTrendRangeOptionViewModel
{
    /// <summary>選択時に適用する期間を取得または設定します。</summary>
    public UsageTrendRange Range { get; init; }

    /// <summary>画面へ表示する日本語名を取得または設定します。</summary>
    public string DisplayName { get; init; } = string.Empty;
}
