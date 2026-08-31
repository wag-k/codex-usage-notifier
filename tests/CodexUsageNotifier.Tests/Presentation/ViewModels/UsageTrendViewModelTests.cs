using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Presentation.Trends;
using CodexUsageNotifier.Presentation.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Presentation.ViewModels;

/// <summary>
/// 使用率推移の非同期初回読み込み、期間切替、およびリアルタイム更新を検証します。
/// </summary>
[TestClass]
public sealed class UsageTrendViewModelTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 31, 12, 0, 0, TimeSpan.Zero);

    /// <summary>既定期間が7日で4つの固定選択肢を持つことを検証します。</summary>
    [TestMethod]
    public void Constructor_DefaultsToSevenDaysAndFourRanges()
    {
        using UsageTrendViewModel viewModel = CreateViewModel(new StubUsageHistoryReader());

        Assert.AreEqual(UsageTrendRange.Days7, viewModel.SelectedRange);
        CollectionAssert.AreEqual(
            new[] { "24時間", "7日", "30日", "90日" },
            viewModel.RangeOptions.Select(option => option.DisplayName).ToArray());
    }

    /// <summary>初回表示で90日分を1回だけ読み込み、再表示時に再読込しないことを検証します。</summary>
    [TestMethod]
    public async Task EnsureLoadedAsync_CalledTwice_ReadsNinetyDaysOnce()
    {
        StubUsageHistoryReader reader = new()
        {
            Entries = [CreateEntry(NowUtc.AddDays(-1), RateLimitClassification.Weekly, 68)],
        };
        using UsageTrendViewModel viewModel = CreateViewModel(reader);

        await viewModel.EnsureLoadedAsync(CancellationToken.None);
        await viewModel.EnsureLoadedAsync(CancellationToken.None);

        Assert.AreEqual(1, reader.CallCount);
        Assert.AreEqual(NowUtc.AddDays(-90), reader.FromUtc);
        Assert.AreEqual(NowUtc, reader.ToUtc);
        Assert.AreEqual(1, viewModel.WeeklyPoints.Count);
    }

    /// <summary>期間切替がメモリ上だけで行われReaderを再実行しないことを検証します。</summary>
    [TestMethod]
    public async Task SelectedRange_Changed_FiltersMemoryWithoutReadingAgain()
    {
        StubUsageHistoryReader reader = new()
        {
            Entries =
            [
                CreateEntry(NowUtc.AddDays(-10), RateLimitClassification.Weekly, 10),
                CreateEntry(NowUtc.AddHours(-12), RateLimitClassification.Weekly, 20),
            ],
        };
        using UsageTrendViewModel viewModel = CreateViewModel(reader);
        await viewModel.EnsureLoadedAsync(CancellationToken.None);

        viewModel.SelectedRange = UsageTrendRange.Hours24;

        Assert.AreEqual(1, reader.CallCount);
        Assert.AreEqual(1, viewModel.WeeklyPoints.Count);
        Assert.AreEqual(20D, viewModel.WeeklyPoints.Single().UsedPercent);
    }

    /// <summary>履歴0件で初回案内を表示することを検証します。</summary>
    [TestMethod]
    public async Task EnsureLoadedAsync_NoHistory_ShowsInitialEmptyMessage()
    {
        using UsageTrendViewModel viewModel = CreateViewModel(new StubUsageHistoryReader());

        await viewModel.EnsureLoadedAsync(CancellationToken.None);

        Assert.IsFalse(viewModel.HasVisiblePoints);
        StringAssert.Contains(viewModel.StatusMessage, "使用履歴はまだありません");
    }

    /// <summary>履歴全体は存在しても選択期間に点がなければ期間別案内を表示することを検証します。</summary>
    [TestMethod]
    public async Task SelectedRange_NoPointsInRange_ShowsRangeEmptyMessage()
    {
        StubUsageHistoryReader reader = new()
        {
            Entries = [CreateEntry(NowUtc.AddDays(-10), RateLimitClassification.FiveHour, 10)],
        };
        using UsageTrendViewModel viewModel = CreateViewModel(reader);
        await viewModel.EnsureLoadedAsync(CancellationToken.None);

        viewModel.SelectedRange = UsageTrendRange.Hours24;

        Assert.AreEqual("この期間の使用履歴はありません。", viewModel.StatusMessage);
    }

    /// <summary>Reader失敗時に安全な画面表示へ切り替えることを検証します。</summary>
    [TestMethod]
    public async Task EnsureLoadedAsync_ReaderFails_ShowsFailureWithoutThrowing()
    {
        StubUsageHistoryReader reader = new() { Exception = new IOException("test") };
        using UsageTrendViewModel viewModel = CreateViewModel(reader);

        await viewModel.EnsureLoadedAsync(CancellationToken.None);

        Assert.AreEqual("履歴を読み込めませんでした", viewModel.StatusMessage);
        Assert.IsFalse(viewModel.IsLoading);
    }

    /// <summary>非同期Readerの完了待ち中も呼び出し元をブロックせずLoadingを表示することを検証します。</summary>
    [TestMethod]
    public async Task EnsureLoadedAsync_PendingReader_ReportsLoading()
    {
        TaskCompletionSource<IReadOnlyList<UsageHistoryEntry>> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        StubUsageHistoryReader reader = new() { PendingResult = completion.Task };
        using UsageTrendViewModel viewModel = CreateViewModel(reader);

        Task loading = viewModel.EnsureLoadedAsync(CancellationToken.None);

        Assert.IsFalse(loading.IsCompleted);
        Assert.IsTrue(viewModel.IsLoading);
        Assert.AreEqual("履歴を読み込んでいます…", viewModel.StatusMessage);
        completion.SetResult([]);
        await loading;
    }

    /// <summary>正常取得を再起動なしで追加し、同一時刻・分類を重複表示しないことを検証します。</summary>
    [TestMethod]
    public void AddSnapshot_SameTimestampTwice_DeduplicatesRealtimePoint()
    {
        using UsageTrendViewModel viewModel = CreateViewModel(new StubUsageHistoryReader());
        UsageSnapshot snapshot = CreateSnapshot(NowUtc, RateLimitClassification.FiveHour, 42);

        viewModel.AddSnapshot(snapshot);
        viewModel.AddSnapshot(snapshot);

        Assert.AreEqual(1, viewModel.FiveHourPoints.Count);
        Assert.AreEqual(42D, viewModel.FiveHourPoints.Single().UsedPercent);
    }

    /// <summary>補助確認間隔の2倍をgap閾値として反映することを検証します。</summary>
    [TestMethod]
    public void UpdatePollingInterval_ValidMinutes_UpdatesGapThreshold()
    {
        using UsageTrendViewModel viewModel = CreateViewModel(new StubUsageHistoryReader());

        viewModel.UpdatePollingInterval(30);

        Assert.AreEqual(TimeSpan.FromHours(1), viewModel.GapThreshold);
    }

    /// <summary>テスト対象のビューモデルを固定現在時刻で生成します。</summary>
    private static UsageTrendViewModel CreateViewModel(StubUsageHistoryReader reader)
    {
        return new UsageTrendViewModel(
            reader,
            new StubTimeProvider(NowUtc),
            NullLogger<UsageTrendViewModel>.Instance);
    }

    /// <summary>テスト用の履歴行を生成します。</summary>
    private static UsageHistoryEntry CreateEntry(
        DateTimeOffset capturedAtUtc,
        RateLimitClassification classification,
        double usedPercent)
    {
        return new UsageHistoryEntry
        {
            CapturedAtUtc = capturedAtUtc,
            RateLimits =
            [
                new RateLimitObservation
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = classification == RateLimitClassification.FiveHour ? 300 : 10080,
                    UsedPercent = usedPercent,
                    Classification = classification,
                },
            ],
        };
    }

    /// <summary>テスト用の正常取得スナップショットを生成します。</summary>
    private static UsageSnapshot CreateSnapshot(
        DateTimeOffset capturedAtUtc,
        RateLimitClassification classification,
        double usedPercent)
    {
        return new UsageSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            RateLimits =
            [
                new RateLimitWindow
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = classification == RateLimitClassification.FiveHour ? 300 : 10080,
                    UsedPercent = usedPercent,
                    RemainingPercent = 100D - usedPercent,
                    Classification = classification,
                },
            ],
        };
    }

    /// <summary>テスト用の履歴Readerです。</summary>
    private sealed class StubUsageHistoryReader : IUsageHistoryReader
    {
        /// <summary>返す履歴を取得または設定します。</summary>
        public IReadOnlyList<UsageHistoryEntry> Entries { get; init; } = Array.Empty<UsageHistoryEntry>();

        /// <summary>発生させる例外を取得または設定します。</summary>
        public Exception? Exception { get; init; }

        /// <summary>任意の完了待ち処理を取得または設定します。</summary>
        public Task<IReadOnlyList<UsageHistoryEntry>>? PendingResult { get; init; }

        /// <summary>呼び出し回数を取得します。</summary>
        public int CallCount { get; private set; }

        /// <summary>最後に指定された開始UTC時刻を取得します。</summary>
        public DateTimeOffset FromUtc { get; private set; }

        /// <summary>最後に指定された終了UTC時刻を取得します。</summary>
        public DateTimeOffset ToUtc { get; private set; }

        /// <summary>設定済みの履歴、待機処理、または例外を返します。</summary>
        public Task<IReadOnlyList<UsageHistoryEntry>> ReadAsync(
            DateTimeOffset fromUtc,
            DateTimeOffset toUtc,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            FromUtc = fromUtc;
            ToUtc = toUtc;
            if (Exception is not null)
            {
                return Task.FromException<IReadOnlyList<UsageHistoryEntry>>(Exception);
            }

            return PendingResult ?? Task.FromResult(Entries);
        }
    }

    /// <summary>固定UTC時刻を返すテスト用TimeProviderです。</summary>
    private sealed class StubTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        /// <summary>返すUTC時刻を受け取ります。</summary>
        public StubTimeProvider(DateTimeOffset utcNow)
        {
            this.utcNow = utcNow;
        }

        /// <summary>固定UTC時刻を返します。</summary>
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
