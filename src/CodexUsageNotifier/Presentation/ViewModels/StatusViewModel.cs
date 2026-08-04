using System.ComponentModel;
using System.Runtime.CompilerServices;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Domain.Services;

namespace CodexUsageNotifier.Presentation.ViewModels;

/// <summary>
/// 基本状態画面に表示する値を管理します。
/// </summary>
public sealed class StatusViewModel : INotifyPropertyChanged, IUsageStatusSink
{
    private string fiveHourRateLimit = "未観測";
    private string weeklyRateLimit = "未観測";
    private string allRateLimits = "未取得";
    private string notificationTarget = "未選択";
    private string resetCredits = "未取得";
    private string monitoringStatus = "開始待ち";
    private string lastSuccessfulFetch = "未取得";
    private string nextCheck = "未設定（Phase 3で実装）";
    private string gmailStatus = "未設定（Phase 4で実装）";
    private string lastNotification = "通知実績なし";
    private string consecutiveFailures = "0回";
    private NotificationTargetSelectionMode notificationTargetSelectionMode;

    /// <summary>
    /// 表示値が変更されたときに発生します。
    /// </summary>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 5時間枠の表示文字列を取得します。
    /// </summary>
    public string FiveHourRateLimit
    {
        get => fiveHourRateLimit;
        private set => SetProperty(ref fiveHourRateLimit, value);
    }

    /// <summary>
    /// 週間枠の表示文字列を取得します。
    /// </summary>
    public string WeeklyRateLimit
    {
        get => weeklyRateLimit;
        private set => SetProperty(ref weeklyRateLimit, value);
    }

    /// <summary>
    /// 取得できたすべての利用枠の表示文字列を取得します。
    /// </summary>
    public string AllRateLimits
    {
        get => allRateLimits;
        private set => SetProperty(ref allRateLimits, value);
    }

    /// <summary>
    /// 将来の通知処理で使用する現在の選択候補を取得します。
    /// </summary>
    public string NotificationTarget
    {
        get => notificationTarget;
        private set => SetProperty(ref notificationTarget, value);
    }

    /// <summary>
    /// リセット回数の表示文字列を取得します。
    /// </summary>
    public string ResetCredits
    {
        get => resetCredits;
        private set => SetProperty(ref resetCredits, value);
    }

    /// <summary>
    /// 監視状態の表示文字列を取得します。
    /// </summary>
    public string MonitoringStatus
    {
        get => monitoringStatus;
        private set => SetProperty(ref monitoringStatus, value);
    }

    /// <summary>
    /// 最終取得時刻の表示文字列を取得します。
    /// </summary>
    public string LastSuccessfulFetch
    {
        get => lastSuccessfulFetch;
        private set => SetProperty(ref lastSuccessfulFetch, value);
    }

    /// <summary>
    /// 次回確認時刻の表示文字列を取得します。
    /// </summary>
    public string NextCheck
    {
        get => nextCheck;
        private set => SetProperty(ref nextCheck, value);
    }

    /// <summary>
    /// Gmail認証状態の表示文字列を取得します。
    /// </summary>
    public string GmailStatus
    {
        get => gmailStatus;
        private set => SetProperty(ref gmailStatus, value);
    }

    /// <summary>
    /// 最終通知結果の表示文字列を取得します。
    /// </summary>
    public string LastNotification
    {
        get => lastNotification;
        private set => SetProperty(ref lastNotification, value);
    }

    /// <summary>
    /// 連続失敗回数の表示文字列を取得します。
    /// </summary>
    public string ConsecutiveFailures
    {
        get => consecutiveFailures;
        private set => SetProperty(ref consecutiveFailures, value);
    }

    /// <summary>
    /// 永続化済みの設定と状態を画面表示へ反映します。
    /// </summary>
    /// <param name="settings">読み込んだ設定です。</param>
    /// <param name="state">読み込んだ状態です。</param>
    public void Initialize(AppSettings settings, ApplicationState state)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(state);

        notificationTargetSelectionMode = settings.NotificationTarget.Mode;
        RateLimitWindow? selectedTarget = state.LastUsageSnapshot is null
            ? null
            : NotificationTargetSelector.Select(state.LastUsageSnapshot.RateLimits, settings.NotificationTarget);
        ApplyUsageSnapshot(state.LastUsageSnapshot, selectedTarget, settings.NotificationTarget.Mode);
        LastSuccessfulFetch = FormatLocalDateTime(state.LastSuccessfulFetchAtUtc, "未取得");
        GmailStatus = settings.GmailNotificationEnabled
            ? "有効・未認証（Phase 4で認証を実装）"
            : "無効（Phase 4で設定を実装）";
        LastNotification = FormatLastNotification(state);
        ConsecutiveFailures = $"{state.ConsecutiveFailures}回";
    }

    /// <summary>
    /// 利用枠の取得開始をUIスレッドへ通知します。
    /// </summary>
    public void SetChecking()
    {
        RunOnUiThread(() => MonitoringStatus = "利用枠を確認中…");
    }

    /// <summary>
    /// 正常に取得した利用枠をUIスレッドへ反映します。
    /// </summary>
    /// <param name="snapshot">取得した利用枠です。</param>
    /// <param name="notificationTarget">現在の設定で選択された将来の通知対象です。</param>
    public void SetSnapshot(UsageSnapshot snapshot, RateLimitWindow? notificationTarget)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        RunOnUiThread(() =>
        {
            ApplyUsageSnapshot(
                snapshot,
                notificationTarget,
                notificationTargetSelectionMode);
            LastSuccessfulFetch = FormatLocalDateTime(snapshot.CapturedAtUtc, "未取得");
            MonitoringStatus = "監視中（App Server接続済み）";
            ConsecutiveFailures = "0回";
        });
    }

    /// <summary>
    /// 利用枠取得の失敗をUIスレッドへ反映します。
    /// </summary>
    /// <param name="consecutiveFailures">現在の連続失敗回数です。</param>
    /// <param name="message">機密情報を含まないエラー概要です。</param>
    public void SetFailure(int consecutiveFailures, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        RunOnUiThread(() =>
        {
            MonitoringStatus = $"再接続待ち：{message}";
            ConsecutiveFailures = $"{consecutiveFailures}回";
        });
    }

    /// <summary>
    /// 保存済みの利用枠があれば基本表示へ反映します。
    /// </summary>
    /// <param name="snapshot">保存済みの利用枠です。</param>
    /// <param name="selectedNotificationTarget">現在選択されている将来の通知対象です。</param>
    /// <param name="selectionMode">通知対象の選択方法です。</param>
    private void ApplyUsageSnapshot(
        UsageSnapshot? snapshot,
        RateLimitWindow? selectedNotificationTarget,
        NotificationTargetSelectionMode selectionMode)
    {
        if (snapshot is null)
        {
            return;
        }

        FiveHourRateLimit = FormatRateLimit(snapshot.FiveHourCandidate, "未観測");
        WeeklyRateLimit = FormatRateLimit(snapshot.WeeklyCandidate, "未観測");
        AllRateLimits = FormatAllRateLimits(snapshot.RateLimits);
        NotificationTarget = FormatNotificationTarget(selectedNotificationTarget, selectionMode);
        ResetCredits = snapshot.ResetCredits?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "未取得";
    }

    /// <summary>
    /// 利用制限枠を画面向けの文字列へ変換します。
    /// </summary>
    /// <param name="window">表示する利用制限枠です。</param>
    /// <returns>残量、使用率、リセット時刻を含む文字列です。</returns>
    private static string FormatRateLimit(RateLimitWindow? window, string emptyText)
    {
        if (window is null)
        {
            return emptyText;
        }

        string reset = FormatLocalDateTime(window.ResetsAtUtc, "不明");
        return $"残り {window.RemainingPercent:0.#}% / 使用 {window.UsedPercent:0.#}% / 次回リセット {reset}";
    }

    /// <summary>
    /// すべての利用枠を診断可能な表示文字列へ変換します。
    /// </summary>
    /// <param name="windows">取得したすべての利用枠です。</param>
    /// <returns>limitId、位置、分類、ウィンドウ長、および利用状況を含む表示文字列です。</returns>
    private static string FormatAllRateLimits(IReadOnlyList<RateLimitWindow> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        if (windows.Count == 0)
        {
            return "利用枠なし";
        }

        return string.Join(
            Environment.NewLine,
            windows.Select(window =>
                $"LimitId={window.LimitId ?? "不明"}, Name={window.LimitName ?? "不明"}, Position={window.Position}, Classification={window.Classification}, Duration={window.WindowDurationMinutes?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "不明"}分, 残り{window.RemainingPercent:0.#}%, 使用{window.UsedPercent:0.#}%, Reset={FormatLocalDateTime(window.ResetsAtUtc, "不明")}, Plan={window.PlanType ?? "不明"}, Reached={window.RateLimitReachedType ?? "なし"}"));
    }

    /// <summary>
    /// 現在の通知対象候補を表示用文字列へ変換します。
    /// </summary>
    /// <param name="window">選択された利用枠です。</param>
    /// <param name="selectionMode">通知対象の選択方法です。</param>
    /// <returns>選択方法と利用枠の識別値を含む表示文字列です。</returns>
    private static string FormatNotificationTarget(
        RateLimitWindow? window,
        NotificationTargetSelectionMode selectionMode)
    {
        string mode = selectionMode == NotificationTargetSelectionMode.Automatic ? "自動" : "手動";
        if (window is null)
        {
            return $"{mode}：対象枠は未観測";
        }

        return $"{mode}：LimitId={window.LimitId ?? "不明"}, Position={window.Position}, Duration={window.WindowDurationMinutes?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "不明"}分, Classification={window.Classification}";
    }

    /// <summary>
    /// WPFのUIスレッド上で表示更新処理を実行します。
    /// </summary>
    /// <param name="action">実行する表示更新処理です。</param>
    private static void RunOnUiThread(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        _ = dispatcher.BeginInvoke(action);
    }

    /// <summary>
    /// UTC時刻をWindowsのローカル時刻表示へ変換します。
    /// </summary>
    /// <param name="value">UTCとして保存された時刻です。</param>
    /// <param name="emptyText">値がない場合の表示です。</param>
    /// <returns>ローカル時刻の表示文字列です。</returns>
    private static string FormatLocalDateTime(DateTimeOffset? value, string emptyText)
    {
        return value?.ToLocalTime().ToString("yyyy/MM/dd HH:mm:ss", System.Globalization.CultureInfo.CurrentCulture)
            ?? emptyText;
    }

    /// <summary>
    /// Windows通知とGmail通知のうち直近の結果を表示用に整形します。
    /// </summary>
    /// <param name="state">通知結果を含むアプリケーション状態です。</param>
    /// <returns>最終通知の表示文字列です。</returns>
    private static string FormatLastNotification(ApplicationState state)
    {
        DeliveryResultState? latest = new[] { state.WindowsDeliveryResult, state.GmailDeliveryResult }
            .Where(result => result?.AttemptedAtUtc is not null)
            .OrderByDescending(result => result!.AttemptedAtUtc)
            .FirstOrDefault();

        if (latest?.AttemptedAtUtc is null)
        {
            return "通知実績なし";
        }

        string status = latest.Status switch
        {
            DeliveryStatus.Succeeded => "成功",
            DeliveryStatus.Failed => "失敗",
            _ => "未実行",
        };
        return $"{FormatLocalDateTime(latest.AttemptedAtUtc, "時刻不明")} / {status}";
    }

    /// <summary>
    /// 値を更新し、必要な場合だけ変更通知を発行します。
    /// </summary>
    /// <typeparam name="T">更新する値の型です。</typeparam>
    /// <param name="field">更新対象のフィールドです。</param>
    /// <param name="value">新しい値です。</param>
    /// <param name="propertyName">変更されたプロパティ名です。</param>
    private void SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
