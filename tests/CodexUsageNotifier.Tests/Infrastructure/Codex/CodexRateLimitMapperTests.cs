using System.Text.Json;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Codex;

namespace CodexUsageNotifier.Tests.Infrastructure.Codex;

/// <summary>
/// Codex利用枠レスポンスの全枠保持、位置保持、および分類を検証します。
/// </summary>
[TestClass]
public sealed class CodexRateLimitMapperTests
{
    /// <summary>
    /// primaryとsecondaryを位置として保持し、ウィンドウ長だけで分類することを検証します。
    /// </summary>
    [TestMethod]
    public void Map_ClassifiesCurrentBucketByDurationWithoutChangingPosition()
    {
        CodexRateLimitResponse response = Deserialize("""
            {
              "rateLimits": {
                "limitId": "legacy",
                "primary": { "usedPercent": 99, "windowDurationMins": 300 }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "limitName": "Codex",
                  "planType": "plus",
                  "rateLimitReachedType": "rate_limit_reached",
                  "primary": { "usedPercent": 21, "windowDurationMins": 10080, "resetsAt": 1785859200 },
                  "secondary": { "usedPercent": 7, "windowDurationMins": 300, "resetsAt": 1785772800 }
                }
              },
              "rateLimitResetCredits": { "availableCount": 2 },
              "futureField": { "ignored": true }
            }
            """);

        UsageSnapshot result = CodexRateLimitMapper.Map(
            response,
            UsageCheckTrigger.Manual,
            new DateTimeOffset(2026, 8, 4, 0, 0, 0, TimeSpan.Zero));

        Assert.AreEqual(2, result.RateLimits.Count);
        RateLimitWindow primary = result.RateLimits.Single(window => window.Position == RateLimitPosition.Primary);
        RateLimitWindow secondary = result.RateLimits.Single(window => window.Position == RateLimitPosition.Secondary);
        Assert.AreEqual(RateLimitClassification.Weekly, primary.Classification);
        Assert.AreEqual(21D, primary.UsedPercent);
        Assert.AreEqual(RateLimitClassification.FiveHour, secondary.Classification);
        Assert.AreEqual(7D, secondary.UsedPercent);
        Assert.AreEqual("plus", primary.PlanType);
        Assert.AreEqual("rate_limit_reached", primary.RateLimitReachedType);
        Assert.AreEqual(2, result.ResetCredits);
    }

    /// <summary>
    /// 現在形式に含まれるすべてのlimitIdを保持し、後方互換ミラーを重複追加しないことを検証します。
    /// </summary>
    [TestMethod]
    public void Map_CurrentBucketsExist_RetainsAllLimitIdsWithoutLegacyMirror()
    {
        CodexRateLimitResponse response = Deserialize("""
            {
              "rateLimits": {
                "limitId": "legacy-codex",
                "primary": { "usedPercent": 40, "windowDurationMins": 300 }
              },
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "primary": { "usedPercent": 50, "windowDurationMins": 10080 }
                },
                "other": {
                  "limitId": "other",
                  "primary": { "usedPercent": 60, "windowDurationMins": 60 }
                }
              }
            }
            """);

        UsageSnapshot result = CodexRateLimitMapper.Map(
            response,
            UsageCheckTrigger.Startup,
            DateTimeOffset.UnixEpoch);

        Assert.AreEqual(2, result.RateLimits.Count);
        CollectionAssert.AreEquivalent(
            new[] { "codex", "other" },
            result.RateLimits.Select(window => window.LimitId).ToArray());
        Assert.IsFalse(result.RateLimits.Any(window => window.LimitId == "legacy-codex"));
    }

    /// <summary>
    /// 複数の同じ既知長と未知長を破棄せず、それぞれの長さに従って分類することを検証します。
    /// </summary>
    [TestMethod]
    public void Map_DuplicateKnownDurationsAndUnknownDuration_RetainsEveryWindow()
    {
        CodexRateLimitResponse response = Deserialize("""
            {
              "rateLimits": {},
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "primary": { "usedPercent": 1, "windowDurationMins": 300 },
                  "secondary": { "usedPercent": 2, "windowDurationMins": 300 }
                },
                "future": {
                  "limitId": "future",
                  "primary": { "usedPercent": 3, "windowDurationMins": 1440 }
                }
              }
            }
            """);

        UsageSnapshot result = CodexRateLimitMapper.Map(
            response,
            UsageCheckTrigger.Scheduled,
            DateTimeOffset.UnixEpoch);

        Assert.AreEqual(3, result.RateLimits.Count);
        Assert.AreEqual(
            2,
            result.RateLimits.Count(window => window.Classification == RateLimitClassification.FiveHour));
        Assert.AreEqual(
            1,
            result.RateLimits.Count(window => window.Classification == RateLimitClassification.Unknown));
    }

    /// <summary>
    /// 実アカウントで観測したprimaryが週間枠でsecondaryがない構成を正常値として変換します。
    /// </summary>
    [TestMethod]
    public void Map_ObservedPrimaryWeeklyWithoutSecondary_DoesNotRequireFiveHourWindow()
    {
        CodexRateLimitResponse response = Deserialize("""
            {
              "rateLimits": {},
              "rateLimitsByLimitId": {
                "codex": {
                  "limitId": "codex",
                  "primary": { "usedPercent": 35, "windowDurationMins": 10080 },
                  "secondary": null
                }
              },
              "rateLimitResetCredits": { "availableCount": 1 }
            }
            """);

        UsageSnapshot result = CodexRateLimitMapper.Map(
            response,
            UsageCheckTrigger.Manual,
            DateTimeOffset.UnixEpoch);

        Assert.AreEqual(1, result.RateLimits.Count);
        Assert.IsNull(result.FiveHourCandidate);
        Assert.IsNotNull(result.WeeklyCandidate);
        Assert.AreEqual(RateLimitPosition.Primary, result.WeeklyCandidate.Position);
        Assert.AreEqual(10080, result.WeeklyCandidate.WindowDurationMinutes);
        Assert.AreEqual(1, result.ResetCredits);
    }

    /// <summary>
    /// 現在形式がない場合だけ後方互換バケットを使用することを検証します。
    /// </summary>
    [TestMethod]
    public void Map_CurrentBucketsMissing_UsesLegacyBucket()
    {
        CodexRateLimitResponse response = Deserialize("""
            {
              "rateLimits": {
                "limitId": "legacy-codex",
                "primary": { "usedPercent": 40, "windowDurationMins": 300 }
              }
            }
            """);

        UsageSnapshot result = CodexRateLimitMapper.Map(
            response,
            UsageCheckTrigger.Startup,
            DateTimeOffset.UnixEpoch);

        Assert.AreEqual(1, result.RateLimits.Count);
        Assert.AreEqual("legacy-codex", result.RateLimits[0].LimitId);
        Assert.AreEqual(RateLimitPosition.Primary, result.RateLimits[0].Position);
        Assert.AreEqual(RateLimitClassification.FiveHour, result.RateLimits[0].Classification);
    }

    /// <summary>
    /// JSON文字列をApp Serverレスポンス型へ変換します。
    /// </summary>
    /// <param name="json">テスト用JSONです。</param>
    /// <returns>変換したレスポンスです。</returns>
    private static CodexRateLimitResponse Deserialize(string json)
    {
        return JsonSerializer.Deserialize<CodexRateLimitResponse>(json)
            ?? throw new InvalidOperationException("テスト用JSONを解釈できませんでした。");
    }
}
