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
    private string notificationTarget = "未取得";
    private string resetCredits = "未取得";
    private string monitoringStatus = "開始待ち";
    private string lastSuccessfulFetch = "未取得";
    private string nextCheck = "未設定";
    private string gmailStatus = "未認証（Phase 4Bで実装）";
    private string lastNotification = "通知実績なし";
    private string consecutiveFailures = "0回";

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
    /// 観測中の全利用枠に適用される通知設定の概要を取得します。
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

        ApplyUsageSnapshot(
            state.LastUsageSnapshot,
            state,
            settings);
        LastSuccessfulFetch = FormatLocalDateTime(state.LastSuccessfulFetchAtUtc, "未取得");
        GmailStatus = settings.GmailNotificationEnabled
            ? "有効・未認証（Phase 4Bで認証を実装）"
            : "無効・未認証（Phase 4Bで認証を実装）";
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
    /// <param name="state">通知状態と直近送信結果を含む最新アプリケーション状態です。</param>
    /// <param name="settings">利用枠別通知設定と閾値です。</param>
    public void SetSnapshot(
        UsageSnapshot snapshot,
        ApplicationState state,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        RunOnUiThread(() =>
        {
            ApplyUsageSnapshot(snapshot, state, settings);
            LastSuccessfulFetch = FormatLocalDateTime(snapshot.CapturedAtUtc, "未取得");
            LastNotification = FormatLastNotification(state);
            MonitoringStatus = "監視中（App Server接続済み）";
            ConsecutiveFailures = "0回";
        });
    }

    /// <summary>
    /// 次回確認予定時刻をUIスレッドへ反映します。
    /// </summary>
    /// <param name="nextCheckAtUtc">次回確認UTC時刻です。予約がなければnullです。</param>
    public void SetNextCheck(DateTimeOffset? nextCheckAtUtc)
    {
        RunOnUiThread(() => NextCheck = FormatLocalDateTime(nextCheckAtUtc, "未設定"));
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
    /// <param name="state">利用枠別の通知状態と回復状態です。</param>
    /// <param name="settings">利用枠別通知設定と閾値です。</param>
    private void ApplyUsageSnapshot(
        UsageSnapshot? snapshot,
        ApplicationState state,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        if (snapshot is null)
        {
            return;
        }

        FiveHourRateLimit = FormatRateLimit(snapshot.FiveHourCandidate, "5時間枠：未観測", snapshot.CapturedAtUtc);
        WeeklyRateLimit = FormatRateLimit(snapshot.WeeklyCandidate, "週間枠：未観測", snapshot.CapturedAtUtc);
        AllRateLimits = FormatAllRateLimits(
            snapshot,
            state,
            settings);
        NotificationTarget = FormatNotificationSettings(snapshot, settings);
        ResetCredits = snapshot.ResetCredits?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "未取得";
    }

    /// <summary>
    /// 利用制限枠を画面向けの文字列へ変換します。
    /// </summary>
    /// <param name="window">表示する利用制限枠です。</param>
    /// <returns>残量、使用率、リセット時刻を含む文字列です。</returns>
    private static string FormatRateLimit(
        RateLimitWindow? window,
        string emptyText,
        DateTimeOffset capturedAtUtc)
    {
        if (window is null)
        {
            return emptyText;
        }

        string reset = FormatLocalDateTime(window.ResetsAtUtc, "リセット時刻未取得");
        string remainingTime = FormatRemainingTime(window.ResetsAtUtc, capturedAtUtc);
        return $"残り {window.RemainingPercent:0.#}% / 使用 {window.UsedPercent:0.#}% / 次回リセット {reset} / あと {remainingTime}";
    }

    /// <summary>
    /// すべての利用枠を診断可能な表示文字列へ変換します。
    /// </summary>
    /// <param name="snapshot">取得したすべての利用枠と取得時刻です。</param>
    /// <param name="state">利用枠ごとの通知状態と回復状態です。</param>
    /// <param name="settings">利用枠別通知設定です。</param>
    /// <returns>limitId、位置、分類、ウィンドウ長、および利用状況を含む表示文字列です。</returns>
    private static string FormatAllRateLimits(
        UsageSnapshot snapshot,
        ApplicationState state,
        AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(settings);
        if (snapshot.RateLimits.Count == 0)
        {
            return "利用枠なし";
        }

        return string.Join(
            Environment.NewLine,
            snapshot.RateLimits.Select(window =>
            {
                RateLimitNotificationSetting windowSetting = RateLimitNotificationSettingsResolver.Resolve(
                    window,
                    settings);
                string enabledTypes = FormatEnabledNotificationTypes(windowSetting);
                string lastNotification = FormatLastWindowNotification(window, state.RateLimitNotificationStates);
                string resetReason = FormatLastResetCompletionReason(window, state.RateLimitNotificationStates);
                string recoverySequence = FormatRecoverySequence(window, state.RateLimitRecoveryStates);
                string resetStatus = window.ResetsAtUtc is null ? "リセット時刻未取得" : "取得済み";
                return $"LimitId={window.LimitId ?? "不明"}, Name={window.LimitName ?? "不明"}, Position={window.Position}, Classification={window.Classification}, Duration={window.WindowDurationMinutes?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "不明"}分, 残り{window.RemainingPercent:0.#}%, 使用{window.UsedPercent:0.#}%, 通知設定={(windowSetting.IsAnyEnabled ? "有効" : "通知対象外")}, 有効通知={enabledTypes}, resetsAt={resetStatus}, Reset={FormatLocalDateTime(window.ResetsAtUtc, "リセット時刻未取得")}, リセットまで={FormatRemainingTime(window.ResetsAtUtc, snapshot.CapturedAtUtc)}, 最終通知={lastNotification}, 最終リセット判定={resetReason}, 回復連番={recoverySequence}, Plan={window.PlanType ?? "不明"}, Reached={window.RateLimitReachedType ?? "なし"}";
            }));
    }

    /// <summary>
    /// リセット時刻までの残り時間を画面表示用に整形します。
    /// </summary>
    /// <param name="resetsAtUtc">次回リセットUTC時刻です。</param>
    /// <param name="capturedAtUtc">表示基準となる取得UTC時刻です。</param>
    /// <returns>日・時間・分による残り時間です。</returns>
    private static string FormatRemainingTime(
        DateTimeOffset? resetsAtUtc,
        DateTimeOffset capturedAtUtc)
    {
        if (resetsAtUtc is null)
        {
            return "リセット時刻未取得";
        }

        TimeSpan remaining = resetsAtUtc.Value - capturedAtUtc;
        if (remaining <= TimeSpan.Zero)
        {
            return "リセット予定時刻経過";
        }

        return remaining.TotalDays >= 1D
            ? $"{(int)remaining.TotalDays}日{remaining.Hours}時間{remaining.Minutes}分"
            : $"{(int)remaining.TotalHours}時間{remaining.Minutes}分";
    }

    /// <summary>
    /// 利用枠別設定から有効な通知種類を表示用に整形します。
    /// </summary>
    /// <param name="setting">表示する利用枠別設定です。</param>
    /// <returns>有効な通知種類、または「なし」です。</returns>
    private static string FormatEnabledNotificationTypes(RateLimitNotificationSetting setting)
    {
        ArgumentNullException.ThrowIfNull(setting);
        List<string> types = [];
        if (setting.ShortWindowRecoveryEnabled)
        {
            types.Add("ShortWindowRecovered");
        }

        if (setting.LongWindowEarlyWarningEnabled)
        {
            types.Add("Early");
        }

        if (setting.LongWindowStandardWarningEnabled)
        {
            types.Add("Standard");
        }

        if (setting.LongWindowFinalWarningEnabled)
        {
            types.Add("Final");
        }

        if (setting.LongWindowResetCompletedEnabled)
        {
            types.Add("LongWindowResetCompleted");
        }

        return types.Count == 0 ? "なし" : string.Join("/", types);
    }

    /// <summary>
    /// 指定利用枠で最後に成功したWindows通知を整形します。
    /// </summary>
    /// <param name="window">表示対象の利用枠です。</param>
    /// <param name="notificationStates">保存済み通知状態です。</param>
    /// <returns>通知種類、段階、送信時刻、または「なし」です。</returns>
    private static string FormatLastWindowNotification(
        RateLimitWindow window,
        IReadOnlyList<RateLimitNotificationState> notificationStates)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(notificationStates);
        RateLimitNotificationState? latest = notificationStates
            .Where(state =>
                state.WindowsDeliveryStatus == DeliveryStatus.Succeeded
                && HasSameIdentity(state, window))
            .OrderByDescending(state => state.DeliveredAtUtc)
            .FirstOrDefault();
        return latest is null
            ? "なし"
            : $"{latest.NotificationType}/{latest.NotificationStage} {FormatLocalDateTime(latest.DeliveredAtUtc, "時刻不明")}";
    }

    /// <summary>
    /// 指定利用枠の最後のリセット完了判定理由を整形します。
    /// </summary>
    /// <param name="window">表示対象の利用枠です。</param>
    /// <param name="notificationStates">保存済み通知状態です。</param>
    /// <returns>最後の判定理由、または「なし」です。</returns>
    private static string FormatLastResetCompletionReason(
        RateLimitWindow window,
        IReadOnlyList<RateLimitNotificationState> notificationStates)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(notificationStates);
        return notificationStates
            .Where(state =>
                state.NotificationType == RateLimitNotificationType.LongWindowResetCompleted
                && state.ResetCompletionReason is not null
                && HasSameIdentity(state, window))
            .OrderByDescending(state => state.ConditionMetAtUtc)
            .Select(state => state.ResetCompletionReason!.Value.ToString())
            .FirstOrDefault() ?? "なし";
    }

    /// <summary>
    /// 指定利用枠の永続回復連番を整形します。
    /// </summary>
    /// <param name="window">表示対象の利用枠です。</param>
    /// <param name="recoveryStates">保存済み回復状態です。</param>
    /// <returns>回復連番です。未観測の場合は0です。</returns>
    private static string FormatRecoverySequence(
        RateLimitWindow window,
        IReadOnlyList<RateLimitRecoveryState> recoveryStates)
    {
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(recoveryStates);
        int sequence = recoveryStates
            .Where(state =>
                string.Equals(state.LimitId, window.LimitId, StringComparison.Ordinal)
                && state.Position == window.Position
                && state.WindowDurationMinutes == window.WindowDurationMinutes)
            .Select(state => state.RecoverySequence)
            .FirstOrDefault();
        return sequence.ToString(System.Globalization.CultureInfo.CurrentCulture);
    }

    /// <summary>
    /// 観測中の全利用枠に適用される通知設定を一覧へ整形します。
    /// </summary>
    /// <param name="snapshot">現在観測中の全利用枠です。</param>
    /// <param name="settings">利用枠別の上書き設定です。</param>
    /// <returns>利用枠識別値と有効通知種類の一覧です。</returns>
    private static string FormatNotificationSettings(UsageSnapshot snapshot, AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(settings);
        if (snapshot.RateLimits.Count == 0)
        {
            return "対象枠は未観測";
        }

        return string.Join(
            Environment.NewLine,
            snapshot.RateLimits.Select(window =>
            {
                RateLimitNotificationSetting windowSetting = RateLimitNotificationSettingsResolver.Resolve(
                    window,
                    settings);
                return $"LimitId={window.LimitId ?? "不明"}, Position={window.Position}, Duration={window.WindowDurationMinutes?.ToString(System.Globalization.CultureInfo.CurrentCulture) ?? "不明"}分：{(windowSetting.IsAnyEnabled ? FormatEnabledNotificationTypes(windowSetting) : "通知対象外")}";
            }));
    }

    /// <summary>
    /// 通知状態と利用枠が同じ識別値を持つか判定します。
    /// </summary>
    /// <param name="state">比較する通知状態です。</param>
    /// <param name="window">比較する利用枠です。</param>
    /// <returns>3つの識別値が一致する場合はtrueです。</returns>
    private static bool HasSameIdentity(RateLimitNotificationState state, RateLimitWindow window)
    {
        return string.Equals(state.LimitId, window.LimitId, StringComparison.Ordinal)
            && state.Position == window.Position
            && state.WindowDurationMinutes == window.WindowDurationMinutes;
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
            DeliveryStatus.InProgress => "送信中",
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
