using System.Text.Json;
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
        return new UsageSnapshot
        {
            CapturedAtUtc = new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero),
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
