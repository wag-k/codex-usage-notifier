using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Domain.Services;

/// <summary>
/// ローカル時刻として設定された通知禁止時間の判定と終了時刻計算を行います。
/// </summary>
public static class QuietHoursSchedule
{
    /// <summary>
    /// 現在時刻が通知禁止時間内か判定します。
    /// </summary>
    /// <param name="nowUtc">現在のUTC時刻です。</param>
    /// <param name="timeZone">設定時刻を解釈するローカルタイムゾーンです。</param>
    /// <param name="settings">通知禁止時間設定です。</param>
    /// <returns>禁止時間内ならtrueです。</returns>
    public static bool IsQuietHours(
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.QuietHoursEnabled || settings.QuietHoursStart == settings.QuietHoursEnd)
        {
            return false;
        }

        TimeOnly localTime = TimeOnly.FromDateTime(TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime);
        return settings.QuietHoursStart < settings.QuietHoursEnd
            ? localTime >= settings.QuietHoursStart && localTime < settings.QuietHoursEnd
            : localTime >= settings.QuietHoursStart || localTime < settings.QuietHoursEnd;
    }

    /// <summary>
    /// 現在の通知禁止時間が終了するUTC時刻を返します。
    /// </summary>
    /// <param name="nowUtc">現在のUTC時刻です。</param>
    /// <param name="timeZone">設定時刻を解釈するローカルタイムゾーンです。</param>
    /// <param name="settings">通知禁止時間設定です。</param>
    /// <returns>禁止時間内なら終了UTC時刻、それ以外はnullです。</returns>
    public static DateTimeOffset? GetQuietHoursEndUtc(
        DateTimeOffset nowUtc,
        TimeZoneInfo timeZone,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(timeZone);
        ArgumentNullException.ThrowIfNull(settings);
        if (!IsQuietHours(nowUtc, timeZone, settings))
        {
            return null;
        }

        DateTime localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone).DateTime;
        DateOnly endDate = DateOnly.FromDateTime(localNow);
        TimeOnly localTime = TimeOnly.FromDateTime(localNow);
        if (settings.QuietHoursStart > settings.QuietHoursEnd
            && localTime >= settings.QuietHoursStart)
        {
            endDate = endDate.AddDays(1);
        }

        DateTime localEnd = endDate.ToDateTime(settings.QuietHoursEnd, DateTimeKind.Unspecified);
        DateTime utcEnd = TimeZoneInfo.ConvertTimeToUtc(localEnd, timeZone);
        return new DateTimeOffset(utcEnd, TimeSpan.Zero);
    }
}
