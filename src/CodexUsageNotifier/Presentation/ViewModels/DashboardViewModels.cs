using System.Globalization;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Presentation.ViewModels;

/// <summary>
/// ダッシュボード上の表示状態を表します。
/// </summary>
public enum DashboardVisualState
{
    /// <summary>まだ観測できていない状態を表します。</summary>
    Unobserved,

    /// <summary>通常の状態を表します。</summary>
    Normal,

    /// <summary>注意が必要な状態を表します。</summary>
    Warning,

    /// <summary>警告が必要な状態を表します。</summary>
    Danger,

    /// <summary>処理中の状態を表します。</summary>
    Checking,
}

/// <summary>
/// ダッシュボードに表示する1つの利用枠カードを表します。
/// </summary>
public sealed class RateLimitCardViewModel
{
    /// <summary>表示タイトルを取得します。</summary>
    public required string Title { get; init; }

    /// <summary>利用枠を観測できているかを取得します。</summary>
    public bool IsObserved { get; init; }

    /// <summary>円グラフへ渡す正規化済み残量を取得します。</summary>
    public double? RemainingPercent { get; init; }

    /// <summary>正規化済み使用率を取得します。</summary>
    public double? UsedPercent { get; init; }

    /// <summary>残量の表示文字列を取得します。</summary>
    public required string RemainingPercentText { get; init; }

    /// <summary>使用率の表示文字列を取得します。</summary>
    public required string UsedPercentText { get; init; }

    /// <summary>次回リセット時刻の表示文字列を取得します。</summary>
    public required string ResetAtText { get; init; }

    /// <summary>次回リセットまでの残り時間を取得します。</summary>
    public required string RemainingTimeText { get; init; }

    /// <summary>利用枠分類の表示文字列を取得します。</summary>
    public required string ClassificationText { get; init; }

    /// <summary>観測した利用枠分類を取得します。</summary>
    public RateLimitClassification? Classification { get; init; }

    /// <summary>残量に応じた表示状態を取得します。</summary>
    public DashboardVisualState VisualState { get; init; }

    /// <summary>
    /// 観測結果からダッシュボード用カードを生成します。
    /// </summary>
    /// <param name="title">カードの表示タイトルです。</param>
    /// <param name="window">観測した利用枠です。未観測の場合はnullです。</param>
    /// <param name="capturedAtUtc">利用枠を取得したUTC時刻です。</param>
    /// <returns>画面表示専用のカードです。</returns>
    public static RateLimitCardViewModel Create(
        string title,
        RateLimitWindow? window,
        DateTimeOffset capturedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (window is null)
        {
            return CreateUnobserved(title);
        }

        double? remainingPercent = UsageRingValue.Normalize(window.RemainingPercent);
        double? usedPercent = UsageRingValue.Normalize(window.UsedPercent);
        return new RateLimitCardViewModel
        {
            Title = title,
            IsObserved = true,
            RemainingPercent = remainingPercent,
            UsedPercent = usedPercent,
            RemainingPercentText = FormatPercent(remainingPercent),
            UsedPercentText = $"使用率 {FormatPercent(usedPercent)}",
            ResetAtText = window.ResetsAtUtc?.ToLocalTime().ToString(
                "yyyy/MM/dd HH:mm",
                CultureInfo.CurrentCulture) ?? "リセット時刻未取得",
            RemainingTimeText = FormatRemainingTime(window.ResetsAtUtc, capturedAtUtc),
            ClassificationText = RateLimitDisplayFormatter.FormatClassification(window.Classification),
            Classification = window.Classification,
            VisualState = GetVisualState(remainingPercent),
        };
    }

    /// <summary>
    /// 未観測状態のカードを生成します。
    /// </summary>
    /// <param name="title">カードの表示タイトルです。</param>
    /// <returns>ゼロ残量とは区別された未観測カードです。</returns>
    public static RateLimitCardViewModel CreateUnobserved(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        return new RateLimitCardViewModel
        {
            Title = title,
            IsObserved = false,
            RemainingPercent = null,
            UsedPercent = null,
            RemainingPercentText = "--",
            UsedPercentText = "使用率 --",
            ResetAtText = "未観測",
            RemainingTimeText = "利用枠をまだ取得していません",
            ClassificationText = "未観測",
            Classification = null,
            VisualState = DashboardVisualState.Unobserved,
        };
    }

    /// <summary>残量からダッシュボード表示状態を決定します。</summary>
    /// <param name="remainingPercent">正規化済み残量です。</param>
    /// <returns>通常、注意、警告、または未観測です。</returns>
    private static DashboardVisualState GetVisualState(double? remainingPercent)
    {
        if (remainingPercent is null)
        {
            return DashboardVisualState.Unobserved;
        }

        if (remainingPercent < 20D)
        {
            return DashboardVisualState.Danger;
        }

        return remainingPercent < 50D
            ? DashboardVisualState.Warning
            : DashboardVisualState.Normal;
    }

    /// <summary>割合を安全な表示文字列へ変換します。</summary>
    /// <param name="value">正規化済み割合です。</param>
    /// <returns>割合または未取得表示です。</returns>
    private static string FormatPercent(double? value)
    {
        return value?.ToString("0.#'%'", CultureInfo.CurrentCulture) ?? "--";
    }

    /// <summary>リセットまでの時間をカード用に整形します。</summary>
    /// <param name="resetsAtUtc">次回リセットUTC時刻です。</param>
    /// <param name="capturedAtUtc">表示基準の取得UTC時刻です。</param>
    /// <returns>日、時間、分による表示です。</returns>
    private static string FormatRemainingTime(
        DateTimeOffset? resetsAtUtc,
        DateTimeOffset capturedAtUtc)
    {
        if (resetsAtUtc is null)
        {
            return "残り時間を取得できません";
        }

        TimeSpan remaining = resetsAtUtc.Value - capturedAtUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return "リセット予定時刻を経過";
        }

        return remaining.TotalDays >= 1D
            ? $"あと {(int)remaining.TotalDays}日 {remaining.Hours}時間"
            : $"あと {(int)remaining.TotalHours}時間 {remaining.Minutes}分";
    }
}

/// <summary>
/// 円形残量表示へ渡す値を安全な範囲へ変換します。
/// </summary>
public static class UsageRingValue
{
    /// <summary>
    /// 割合を0から100へ制限し、非数や無限大を未取得として扱います。
    /// </summary>
    /// <param name="value">変換する割合です。</param>
    /// <returns>0から100の値、または未取得を表すnullです。</returns>
    public static double? Normalize(double? value)
    {
        if (value is null || double.IsNaN(value.Value) || double.IsInfinity(value.Value))
        {
            return null;
        }

        return Math.Clamp(value.Value, 0D, 100D);
    }
}

/// <summary>
/// ダッシュボードへ表示する直近通知を表します。
/// </summary>
public sealed class RecentNotificationViewModel
{
    /// <summary>通知チャネル名を取得します。</summary>
    public required string Channel { get; init; }

    /// <summary>通知結果の安全な概要を取得します。</summary>
    public required string Summary { get; init; }

    /// <summary>通知試行時刻の表示文字列を取得します。</summary>
    public required string AttemptedAtText { get; init; }

    /// <summary>配送状態の表示文字列を取得します。</summary>
    public required string StatusText { get; init; }

    /// <summary>配送が成功したかを取得します。</summary>
    public bool IsSucceeded { get; init; }
}

/// <summary>
/// メールアドレスを概要画面向けにマスクします。
/// </summary>
public static class EmailAddressMaskFormatter
{
    /// <summary>
    /// ローカル部の先頭文字以外を隠したメールアドレスを返します。
    /// </summary>
    /// <param name="emailAddress">表示するメールアドレスです。</param>
    /// <returns>マスク済みメールアドレス、または未認証表示です。</returns>
    public static string Mask(string? emailAddress)
    {
        if (string.IsNullOrWhiteSpace(emailAddress))
        {
            return "未認証";
        }

        int separatorIndex = emailAddress.IndexOf('@');
        if (separatorIndex <= 0 || separatorIndex == emailAddress.Length - 1)
        {
            return "アカウント情報あり";
        }

        return $"{emailAddress[0]}***{emailAddress[separatorIndex..]}";
    }
}
