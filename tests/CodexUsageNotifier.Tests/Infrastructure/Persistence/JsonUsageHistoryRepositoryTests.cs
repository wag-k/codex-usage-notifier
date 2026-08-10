using System.Text.Json;
using CodexUsageNotifier.Application.Maintenance;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Infrastructure.Persistence;

/// <summary>
/// 全利用枠のJSONL履歴保存と新規観測判定を検証します。
/// </summary>
[TestClass]
public sealed class JsonUsageHistoryRepositoryTests
{
    /// <summary>
    /// 取得成功1回の全利用枠を1行へ保存し、必要な観測項目を保持することを検証します。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_FirstSnapshot_SavesAllWindowsAndReturnsAllAsNew()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        using JsonUsageHistoryRepository repository = CreateRepository(paths);
        UsageSnapshot snapshot = CreateSnapshot(
            CreateWindow("codex", RateLimitPosition.Primary, 10080, 35),
            CreateWindow("future", RateLimitPosition.Secondary, 1440, 12));

        IReadOnlyList<RateLimitObservation> newlyObserved = await repository.AppendAsync(
            snapshot,
            CancellationToken.None);

        Assert.AreEqual(2, newlyObserved.Count);
        string[] lines = await File.ReadAllLinesAsync(paths.UsageHistoryFilePath);
        Assert.AreEqual(1, lines.Length);
        using JsonDocument document = JsonDocument.Parse(lines[0]);
        JsonElement rateLimits = document.RootElement.GetProperty("rateLimits");
        Assert.AreEqual(2, rateLimits.GetArrayLength());
        JsonElement first = rateLimits[0];
        Assert.AreEqual("codex", first.GetProperty("limitId").GetString());
        Assert.AreEqual("Primary", first.GetProperty("position").GetString());
        Assert.AreEqual(10080, first.GetProperty("windowDurationMinutes").GetInt32());
        Assert.AreEqual(35D, first.GetProperty("usedPercent").GetDouble());
        Assert.AreEqual("Weekly", first.GetProperty("classification").GetString());
    }

    /// <summary>
    /// 同じ識別組み合わせを再取得しても新規扱いせず、異なる長さだけを新規として返すことを検証します。
    /// </summary>
    [TestMethod]
    public async Task AppendAsync_ExistingIdentity_ReturnsOnlyNewCombination()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        UsageSnapshot initial = CreateSnapshot(
            CreateWindow("codex", RateLimitPosition.Primary, 10080, 35));
        using (JsonUsageHistoryRepository firstRepository = CreateRepository(paths))
        {
            await firstRepository.AppendAsync(initial, CancellationToken.None);
        }

        using JsonUsageHistoryRepository secondRepository = CreateRepository(paths);
        UsageSnapshot next = CreateSnapshot(
            CreateWindow("codex", RateLimitPosition.Primary, 10080, 40),
            CreateWindow("codex", RateLimitPosition.Secondary, 300, 5));

        IReadOnlyList<RateLimitObservation> newlyObserved = await secondRepository.AppendAsync(
            next,
            CancellationToken.None);

        Assert.AreEqual(1, newlyObserved.Count);
        Assert.AreEqual(RateLimitPosition.Secondary, newlyObserved[0].Position);
        Assert.AreEqual(300, newlyObserved[0].WindowDurationMinutes);
        Assert.AreEqual(2, (await File.ReadAllLinesAsync(paths.UsageHistoryFilePath)).Length);
    }

    /// <summary>
    /// 保持境界より新しい行と境界ちょうどの行を保持し、古い行だけを削除することを検証します。
    /// </summary>
    [TestMethod]
    public async Task MaintainAsync_MixedAges_KeepsBoundaryAndNewerRows()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        using JsonUsageHistoryRepository repository = CreateRepository(paths);
        DateTimeOffset boundary = new(2026, 5, 6, 0, 0, 0, TimeSpan.Zero);
        await repository.AppendAsync(CreateSnapshot(boundary.AddTicks(-1), CreateWindow("old", RateLimitPosition.Primary, 300, 10)), CancellationToken.None);
        await repository.AppendAsync(CreateSnapshot(boundary, CreateWindow("boundary", RateLimitPosition.Primary, 300, 20)), CancellationToken.None);
        await repository.AppendAsync(CreateSnapshot(boundary.AddDays(1), CreateWindow("new", RateLimitPosition.Primary, 300, 30)), CancellationToken.None);

        UsageHistoryMaintenanceResult result = await repository.MaintainAsync(boundary, CancellationToken.None);

        Assert.AreEqual(1, result.DeletedLineCount);
        Assert.AreEqual(2, result.RetainedLineCount);
        string[] lines = await File.ReadAllLinesAsync(paths.UsageHistoryFilePath);
        Assert.AreEqual(2, lines.Length);
        StringAssert.Contains(lines[0], "boundary");
        StringAssert.Contains(lines[1], "new");
    }

    /// <summary>
    /// 複数利用枠を含む1取得行を分割せず、取得時刻単位で削除することを検証します。
    /// </summary>
    [TestMethod]
    public async Task MaintainAsync_OldSnapshotWithMultipleWindows_DeletesWholeLine()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        using JsonUsageHistoryRepository repository = CreateRepository(paths);
        await repository.AppendAsync(
            CreateSnapshot(
                new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
                CreateWindow("codex", RateLimitPosition.Primary, 300, 10),
                CreateWindow("codex", RateLimitPosition.Secondary, 10080, 20)),
            CancellationToken.None);

        UsageHistoryMaintenanceResult result = await repository.MaintainAsync(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.AreEqual(1, result.DeletedLineCount);
        Assert.AreEqual(0, (await File.ReadAllLinesAsync(paths.UsageHistoryFilePath)).Length);
    }

    /// <summary>
    /// JSONとして解釈できない行を無言で削除せず、そのまま保持することを検証します。
    /// </summary>
    [TestMethod]
    public async Task MaintainAsync_CorruptedLine_RetainsOriginalText()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(paths.UsageHistoryFilePath)!);
        const string corruptedLine = "{ this is not valid json";
        await File.WriteAllTextAsync(paths.UsageHistoryFilePath, corruptedLine + Environment.NewLine);
        using JsonUsageHistoryRepository repository = CreateRepository(paths);

        UsageHistoryMaintenanceResult result = await repository.MaintainAsync(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        Assert.AreEqual(1, result.CorruptedLineCount);
        Assert.AreEqual(1, result.RetainedLineCount);
        Assert.AreEqual(corruptedLine, (await File.ReadAllLinesAsync(paths.UsageHistoryFilePath)).Single());
    }

    /// <summary>
    /// キャンセルされた保守が元ファイルを変更せず、一時ファイルも残さないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task MaintainAsync_Canceled_PreservesOriginalFile()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        using JsonUsageHistoryRepository repository = CreateRepository(paths);
        await repository.AppendAsync(CreateSnapshot(CreateWindow("codex", RateLimitPosition.Primary, 300, 10)), CancellationToken.None);
        string original = await File.ReadAllTextAsync(paths.UsageHistoryFilePath);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => repository.MaintainAsync(DateTimeOffset.MaxValue, cancellation.Token));

        Assert.AreEqual(original, await File.ReadAllTextAsync(paths.UsageHistoryFilePath));
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    /// <summary>
    /// 履歴追記と保守を並行要求しても新しい取得行を失わないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task AppendAndMaintainAsync_ConcurrentRequests_DoNotLoseNewEntry()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        using JsonUsageHistoryRepository repository = CreateRepository(paths);
        DateTimeOffset now = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        UsageSnapshot snapshot = CreateSnapshot(now, CreateWindow("concurrent", RateLimitPosition.Primary, 300, 10));

        await Task.WhenAll(
            repository.AppendAsync(snapshot, CancellationToken.None),
            repository.MaintainAsync(now.AddDays(-90), CancellationToken.None));

        string[] lines = await File.ReadAllLinesAsync(paths.UsageHistoryFilePath);
        Assert.AreEqual(1, lines.Length);
        StringAssert.Contains(lines[0], "concurrent");
    }

    /// <summary>
    /// 保守後のobservedKeysが保持履歴だけから再構築されることを検証します。
    /// </summary>
    [TestMethod]
    public async Task MaintainAsync_RemovedObservation_ReappearsAsNew()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        using JsonUsageHistoryRepository repository = CreateRepository(paths);
        RateLimitWindow window = CreateWindow("returning", RateLimitPosition.Primary, 300, 10);
        await repository.AppendAsync(
            CreateSnapshot(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), window),
            CancellationToken.None);
        await repository.MaintainAsync(
            new DateTimeOffset(2026, 5, 1, 0, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        IReadOnlyList<RateLimitObservation> newlyObserved = await repository.AppendAsync(
            CreateSnapshot(new DateTimeOffset(2026, 8, 10, 0, 0, 0, TimeSpan.Zero), window),
            CancellationToken.None);

        Assert.AreEqual(1, newlyObserved.Count);
        Assert.AreEqual("returning", newlyObserved[0].LimitId);
    }

    /// <summary>
    /// 複数の保守要求を安全に直列化し、置換途中のファイルを残さないことを検証します。
    /// </summary>
    [TestMethod]
    public async Task MaintainAsync_ConcurrentMaintenance_SerializesAccess()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        using JsonUsageHistoryRepository repository = CreateRepository(paths);
        DateTimeOffset now = new(2026, 8, 10, 0, 0, 0, TimeSpan.Zero);
        await repository.AppendAsync(CreateSnapshot(now, CreateWindow("codex", RateLimitPosition.Primary, 300, 10)), CancellationToken.None);

        await Task.WhenAll(
            repository.MaintainAsync(now.AddDays(-90), CancellationToken.None),
            repository.MaintainAsync(now.AddDays(-90), CancellationToken.None));

        Assert.AreEqual(1, (await File.ReadAllLinesAsync(paths.UsageHistoryFilePath)).Length);
        Assert.AreEqual(0, Directory.GetFiles(temporaryDirectory.Path, "*.tmp").Length);
    }

    /// <summary>
    /// テスト対象の履歴リポジトリを生成します。
    /// </summary>
    /// <param name="paths">テスト専用の保存先です。</param>
    /// <returns>履歴リポジトリです。</returns>
    private static JsonUsageHistoryRepository CreateRepository(AppDataPaths paths)
    {
        return new JsonUsageHistoryRepository(
            paths,
            NullLogger<JsonUsageHistoryRepository>.Instance);
    }

    /// <summary>
    /// 指定した全利用枠を持つスナップショットを生成します。
    /// </summary>
    /// <param name="windows">保存する利用枠です。</param>
    /// <returns>テスト用スナップショットです。</returns>
    private static UsageSnapshot CreateSnapshot(params RateLimitWindow[] windows)
    {
        return CreateSnapshot(
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero),
            windows);
    }

    /// <summary>
    /// 指定取得時刻と全利用枠を持つスナップショットを生成します。
    /// </summary>
    /// <param name="capturedAtUtc">取得UTC時刻です。</param>
    /// <param name="windows">保存する利用枠です。</param>
    /// <returns>テスト用スナップショットです。</returns>
    private static UsageSnapshot CreateSnapshot(
        DateTimeOffset capturedAtUtc,
        params RateLimitWindow[] windows)
    {
        return new UsageSnapshot
        {
            CapturedAtUtc = capturedAtUtc,
            RateLimits = windows,
            Trigger = UsageCheckTrigger.Manual,
        };
    }

    /// <summary>
    /// テスト用の利用枠を生成します。
    /// </summary>
    /// <param name="limitId">利用枠識別子です。</param>
    /// <param name="position">レスポンス内の位置です。</param>
    /// <param name="durationMinutes">ウィンドウ長です。</param>
    /// <param name="usedPercent">使用率です。</param>
    /// <returns>テスト用利用枠です。</returns>
    private static RateLimitWindow CreateWindow(
        string limitId,
        RateLimitPosition position,
        int durationMinutes,
        double usedPercent)
    {
        return new RateLimitWindow
        {
            LimitId = limitId,
            Position = position,
            WindowDurationMinutes = durationMinutes,
            UsedPercent = usedPercent,
            RemainingPercent = 100D - usedPercent,
            ResetsAtUtc = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
            Classification = durationMinutes switch
            {
                300 => RateLimitClassification.FiveHour,
                10080 => RateLimitClassification.Weekly,
                _ => RateLimitClassification.Unknown,
            },
        };
    }
}
