using System.Globalization;
using System.Text;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>
/// 共通通知候補を1通のGmail本番通知へ整形します。
/// </summary>
public static class GmailNotificationMessageFactory
{
    /// <summary>
    /// 同じ取得処理で成立した通知候補を1通へ集約します。
    /// </summary>
    /// <param name="candidates">集約する共通通知候補です。</param>
    /// <param name="confirmedAtUtc">候補を確認したUTC時刻です。</param>
    /// <param name="localTimeZone">画面表示時刻へ変換するタイムゾーンです。</param>
    /// <returns>日本語UTF-8で送信する件名と本文です。</returns>
    public static GmailNotificationMessage CreateAggregate(
        IReadOnlyList<RateLimitNotificationCandidate> candidates,
        DateTimeOffset confirmedAtUtc,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(localTimeZone);
        if (candidates.Count == 0)
        {
            throw new ArgumentException("Gmail通知候補が1件以上必要です。", nameof(candidates));
        }

        string subject = candidates.Count == 1
            ? $"Codex Usage Notifier: {CreateHeadline(candidates[0])}"
            : $"Codex Usage Notifier: {candidates.Count.ToString(CultureInfo.InvariantCulture)}件のお知らせ";
        StringBuilder body = new();
        if (candidates.Count > 1)
        {
            body.Append("Codex利用枠について")
                .Append(candidates.Count.ToString(CultureInfo.InvariantCulture))
                .AppendLine("件のお知らせがあります。")
                .AppendLine();
        }

        for (int index = 0; index < candidates.Count; index++)
        {
            AppendCandidate(
                body,
                candidates[index],
                index + 1,
                candidates.Count > 1,
                confirmedAtUtc,
                localTimeZone);
            if (index < candidates.Count - 1)
            {
                body.AppendLine();
            }
        }

        body.AppendLine()
            .Append("確認時刻: ")
            .Append(FormatLocalDateTime(confirmedAtUtc, localTimeZone));
        return new GmailNotificationMessage { Subject = subject, Body = body.ToString() };
    }

    /// <summary>1件の候補を本文へ追記します。</summary>
    private static void AppendCandidate(
        StringBuilder body,
        RateLimitNotificationCandidate candidate,
        int number,
        bool includeNumber,
        DateTimeOffset confirmedAtUtc,
        TimeZoneInfo localTimeZone)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentNullException.ThrowIfNull(candidate);
        RateLimitWindow window = candidate.Window;
        if (includeNumber)
        {
            body.Append('[')
                .Append(number.ToString(CultureInfo.InvariantCulture))
                .Append("] ");
        }

        body.AppendLine(CreateHeadline(candidate))
            .Append("通知種別: ").AppendLine(candidate.NotificationType.ToString())
            .Append("通知段階: ").AppendLine(candidate.NotificationStage.ToString())
            .Append("LimitId: ").AppendLine(window.LimitId ?? "(不明)")
            .Append("位置: ").AppendLine(window.Position.ToString())
            .Append("分類: ").AppendLine(window.Classification.ToString())
            .Append("期間: ")
            .Append(window.WindowDurationMinutes?.ToString(CultureInfo.InvariantCulture) ?? "不明")
            .AppendLine("分")
            .Append("残量: ")
            .Append(window.RemainingPercent.ToString("0.##", CultureInfo.InvariantCulture))
            .AppendLine("%")
            .Append("条件成立: ")
            .AppendLine(FormatLocalDateTime(candidate.ConditionMetAtUtc, localTimeZone));

        if (window.ResetsAtUtc is null)
        {
            body.AppendLine("次回リセット: リセット時刻未取得");
        }
        else
        {
            body.Append("次回リセット: ")
                .AppendLine(FormatLocalDateTime(window.ResetsAtUtc.Value, localTimeZone));
            TimeSpan remaining = window.ResetsAtUtc.Value - confirmedAtUtc;
            if (remaining > TimeSpan.Zero)
            {
                body.Append("リセットまで: ")
                    .Append(Math.Ceiling(remaining.TotalHours).ToString(CultureInfo.InvariantCulture))
                    .AppendLine("時間");
            }
        }

        if (candidate.ResetCompletionReason is not null)
        {
            body.Append("リセット完了判定: ")
                .AppendLine(candidate.ResetCompletionReason.Value.ToString());
            if (candidate.ResetCompletionReason == RateLimitResetCompletionReason.UsageDropInference)
            {
                body.AppendLine("使用率の大幅な低下からリセット完了を推定しました。");
            }
        }
    }

    /// <summary>通知候補を人が識別しやすい見出しへ変換します。</summary>
    private static string CreateHeadline(RateLimitNotificationCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return candidate.NotificationType switch
        {
            RateLimitNotificationType.ShortWindowRecovered => "短期枠が回復しました",
            RateLimitNotificationType.LongWindowEarlyWarning => "週間枠 Early",
            RateLimitNotificationType.LongWindowStandardWarning => "週間枠のリセットが近づいています",
            RateLimitNotificationType.LongWindowFinalWarning => "週間枠 Final",
            RateLimitNotificationType.LongWindowResetCompleted => "週間枠の新しい利用期間を確認しました",
            _ => "Codex利用枠のお知らせ",
        };
    }

    /// <summary>UTC時刻を指定タイムゾーンの分精度表示へ変換します。</summary>
    private static string FormatLocalDateTime(DateTimeOffset value, TimeZoneInfo localTimeZone)
    {
        return TimeZoneInfo.ConvertTime(value, localTimeZone)
            .ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);
    }
}
