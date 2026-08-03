using System.Text.Json;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Codex;

namespace CodexUsageNotifier.Tests.Infrastructure.Codex;

/// <summary>
/// Codex利用枠レスポンスの識別と変換を検証します。
/// </summary>
[TestClass]
public sealed class CodexRateLimitMapperTests
{
    /// <summary>
    /// codexの現在形式を優先し、primaryとsecondaryの位置ではなく長さで識別することを検証します。
    /// </summary>
    [TestMethod]
    public void Map_PrefersCodexBucketAndClassifiesByDuration()
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

        Assert.AreEqual("codex", result.RawLimitId);
        Assert.AreEqual(7D, result.Primary!.UsedPercent);
        Assert.AreEqual(RateLimitWindowSource.Secondary, result.Primary.Source);
        Assert.AreEqual(21D, result.Secondary!.UsedPercent);
        Assert.AreEqual(RateLimitWindowSource.Primary, result.Secondary.Source);
        Assert.AreEqual(2, result.ResetCredits);
        Assert.AreEqual(0, result.UnknownWindows.Count);
    }

    /// <summary>
    /// codexの現在形式がない場合に後方互換形式を使用することを検証します。
    /// </summary>
    [TestMethod]
    public void Map_UsesLegacyBucketWhenCodexBucketDoesNotExist()
    {
        CodexRateLimitResponse response = Deserialize("""
            {
              "rateLimits": {
                "limitId": "legacy-codex",
                "primary": { "usedPercent": 40, "windowDurationMins": 300 },
                "secondary": { "usedPercent": 50, "windowDurationMins": 10080 }
              },
              "rateLimitsByLimitId": {
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

        Assert.AreEqual("legacy-codex", result.RawLimitId);
        Assert.AreEqual(60D, result.Primary!.RemainingPercent);
        Assert.AreEqual(50D, result.Secondary!.RemainingPercent);
        Assert.AreEqual(1, result.UnknownWindows.Count);
        Assert.AreEqual("other", result.UnknownWindows[0].LimitId);
    }

    /// <summary>
    /// 未知のウィンドウ長や重複した既知長を破棄せずUnknownとして保持することを検証します。
    /// </summary>
    [TestMethod]
    public void Map_RetainsUnidentifiedAndDuplicateWindowsAsUnknown()
    {
        CodexRateLimitResponse response = Deserialize("""
            {
              "rateLimits": { "limitId": "legacy" },
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

        Assert.IsNotNull(result.Primary);
        Assert.IsNull(result.Secondary);
        Assert.AreEqual(2, result.UnknownWindows.Count);
        Assert.IsTrue(result.UnknownWindows.All(window => window.Kind == RateLimitWindowKind.Unknown));
        CollectionAssert.AreEquivalent(
            new[] { "codex", "future" },
            result.UnknownWindows.Select(window => window.LimitId).ToArray());
    }

    /// <summary>
    /// 実アカウントで観測したprimaryが週間枠でsecondaryがない構成を位置に依存せず変換することを検証します。
    /// </summary>
    [TestMethod]
    public void Map_ObservedPrimaryWeeklyWithoutSecondary_DoesNotInventFiveHourWindow()
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

        Assert.IsNull(result.Primary);
        Assert.IsNotNull(result.Secondary);
        Assert.AreEqual(RateLimitWindowSource.Primary, result.Secondary.Source);
        Assert.AreEqual(10080, result.Secondary.WindowDurationMinutes);
        Assert.AreEqual(1, result.ResetCredits);
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
