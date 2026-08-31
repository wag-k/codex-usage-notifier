using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Tests.Application.Notifications;

/// <summary>
/// Gmail本番通知の件名、集約本文、およびリセット完了判定理由の安全な表現を検証します。
/// </summary>
[TestClass]
public sealed class GmailNotificationMessageFactoryTests
{
    private static readonly DateTimeOffset NowUtc = new(2026, 8, 9, 9, 0, 0, TimeSpan.Zero);

    /// <summary>短期回復メールへ同じ取得の短期枠と週間枠の残量を区別して表示することを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_ShortWindowRecovered_IncludesWeeklyRemaining()
    {
        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            [CreateShortRecoveryCandidate()],
            NowUtc,
            TimeZoneInfo.Utc,
            new RateLimitNotificationDisplayContext { WeeklyRemainingPercent = 63 });

        StringAssert.Contains(message.Body, "短期枠の残量: 65%");
        StringAssert.Contains(message.Body, "週間枠の残量: 63%");
    }

    /// <summary>週間枠を観測できない短期回復メールへ未観測を表示することを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_ShortWindowRecoveredWithoutWeekly_ShowsUnobserved()
    {
        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            [CreateShortRecoveryCandidate()],
            NowUtc,
            TimeZoneInfo.Utc);

        StringAssert.Contains(message.Body, "週間枠の残量: 未観測");
    }

    /// <summary>複数候補メールでも短期回復候補だけへ現在の週間枠残量を追加することを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_MultipleCandidates_AddsWeeklyContextOnlyToShortRecovery()
    {
        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            [CreateShortRecoveryCandidate(), CreateCandidate("team", null)],
            NowUtc,
            TimeZoneInfo.Utc,
            new RateLimitNotificationDisplayContext { WeeklyRemainingPercent = 63 });

        Assert.AreEqual(1, CountOccurrences(message.Body, "週間枠の残量: 63%"));
        StringAssert.Contains(message.Body, "残量: 90%");
    }

    /// <summary>長期枠だけのメールへ短期回復用の週間枠補足を追加しないことを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_LongWindowOnly_DoesNotAddWeeklySupplement()
    {
        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            [CreateCandidate("codex", null)],
            NowUtc,
            TimeZoneInfo.Utc,
            new RateLimitNotificationDisplayContext { WeeklyRemainingPercent = 63 });

        Assert.IsFalse(message.Body.Contains("週間枠の残量:", StringComparison.Ordinal));
        StringAssert.Contains(message.Body, "残量: 90%");
    }

    /// <summary>ResetTimeAdvancedを本文へ明示できることを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_ResetTimeAdvanced_IncludesReason()
    {
        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            [CreateCandidate("codex", RateLimitResetCompletionReason.ResetTimeAdvanced)],
            NowUtc,
            TimeZoneInfo.Utc);

        StringAssert.Contains(message.Body, "リセット完了判定: ResetTimeAdvanced");
    }

    /// <summary>UsageDropInferenceを確定ではなく推定として説明することを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_UsageDropInference_ExplainsInference()
    {
        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            [CreateCandidate("codex", RateLimitResetCompletionReason.UsageDropInference)],
            NowUtc,
            TimeZoneInfo.Utc);

        StringAssert.Contains(message.Body, "リセット完了判定: UsageDropInference");
        StringAssert.Contains(message.Body, "推定しました");
    }

    /// <summary>複数limitIdを1通へ集約して各識別子を保持することを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_MultipleLimitIds_CreatesSingleMessage()
    {
        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            [CreateCandidate("codex", null), CreateCandidate("team", null)],
            NowUtc,
            TimeZoneInfo.Utc);

        Assert.AreEqual("Codex Usage Notifier: 2件のお知らせ", message.Subject);
        StringAssert.Contains(message.Body, "LimitId: codex");
        StringAssert.Contains(message.Body, "LimitId: team");
    }

    /// <summary>本文へOAuthトークン名や認証ヘッダーを出力しないことを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_NormalCandidate_DoesNotContainCredentialFields()
    {
        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            [CreateCandidate("codex", null)],
            NowUtc,
            TimeZoneInfo.Utc);

        Assert.IsFalse(message.Body.Contains("access_token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(message.Body.Contains("refresh_token", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(message.Body.Contains("Authorization", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>本文が未使用分の消滅または繰り越しを断定しないことを検証します。</summary>
    [TestMethod]
    public void CreateAggregate_NormalCandidate_DoesNotClaimRolloverBehavior()
    {
        GmailNotificationMessage message = GmailNotificationMessageFactory.CreateAggregate(
            [CreateCandidate("codex", null)],
            NowUtc,
            TimeZoneInfo.Utc);

        Assert.IsFalse(message.Body.Contains("必ず消滅", StringComparison.Ordinal));
        Assert.IsFalse(message.Body.Contains("繰り越され", StringComparison.Ordinal));
    }

    /// <summary>指定判定理由を持つ週間枠リセット完了候補を生成します。</summary>
    private static RateLimitNotificationCandidate CreateCandidate(
        string limitId,
        RateLimitResetCompletionReason? reason)
    {
        return new RateLimitNotificationCandidate
        {
            Window = new RateLimitWindow
            {
                LimitId = limitId,
                Position = RateLimitPosition.Primary,
                Classification = RateLimitClassification.Weekly,
                WindowDurationMinutes = 10080,
                UsedPercent = 10,
                RemainingPercent = 90,
                ResetsAtUtc = NowUtc.AddDays(7),
            },
            RecoveryWindowId = "reset:next",
            NotificationType = reason is null
                ? RateLimitNotificationType.LongWindowStandardWarning
                : RateLimitNotificationType.LongWindowResetCompleted,
            NotificationStage = reason is null
                ? RateLimitNotificationStage.Standard
                : RateLimitNotificationStage.Completed,
            ConditionMetAtUtc = NowUtc,
            ResetCompletionReason = reason,
        };
    }

    /// <summary>短期枠回復のメッセージ生成に使用する候補を生成します。</summary>
    /// <returns>残量65%の短期回復候補です。</returns>
    private static RateLimitNotificationCandidate CreateShortRecoveryCandidate()
    {
        return new RateLimitNotificationCandidate
        {
            Window = new RateLimitWindow
            {
                LimitId = "codex",
                Position = RateLimitPosition.Primary,
                Classification = RateLimitClassification.FiveHour,
                WindowDurationMinutes = 300,
                UsedPercent = 35,
                RemainingPercent = 65,
                ResetsAtUtc = NowUtc.AddHours(5),
            },
            RecoveryWindowId = "reset:short",
            NotificationType = RateLimitNotificationType.ShortWindowRecovered,
            NotificationStage = RateLimitNotificationStage.Recovered,
            ConditionMetAtUtc = NowUtc,
        };
    }

    /// <summary>本文に指定文字列が現れる回数を返します。</summary>
    /// <param name="source">検索対象です。</param>
    /// <param name="value">検索する文字列です。</param>
    /// <returns>重複しない出現回数です。</returns>
    private static int CountOccurrences(string source, string value)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        int count = 0;
        int startIndex = 0;
        while ((startIndex = source.IndexOf(value, startIndex, StringComparison.Ordinal)) >= 0)
        {
            count++;
            startIndex += value.Length;
        }

        return count;
    }
}
