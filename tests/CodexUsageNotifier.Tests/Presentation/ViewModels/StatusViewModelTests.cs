using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Presentation.ViewModels;
using CodexUsageNotifier.Tests.TestDoubles;

namespace CodexUsageNotifier.Tests.Presentation.ViewModels;

/// <summary>
/// 状態画面の全利用枠表示と未観測表示を検証します。
/// </summary>
[TestClass]
public sealed class StatusViewModelTests
{
    /// <summary>
    /// 週間枠だけを観測した場合に5時間枠を未観測とし、全枠と有効な長期通知を表示します。
    /// </summary>
    [TestMethod]
    public void Initialize_WeeklyOnly_ShowsFiveHourAsUnobservedAndDisplaysAllWindows()
    {
        RateLimitWindow weekly = new()
        {
            LimitId = "codex",
            LimitName = "Codex",
            Position = RateLimitPosition.Primary,
            Classification = RateLimitClassification.Weekly,
            WindowDurationMinutes = 10080,
            UsedPercent = 35,
            RemainingPercent = 65,
            PlanType = "plus",
        };
        UsageSnapshot snapshot = new()
        {
            CapturedAtUtc = DateTimeOffset.UnixEpoch,
            RateLimits = [weekly],
        };
        ApplicationState state = new() { LastUsageSnapshot = snapshot };
        StatusViewModel viewModel = new();

        viewModel.Initialize(AppSettings.CreateDefault(), state);

        Assert.AreEqual("5時間枠：未観測", viewModel.FiveHourRateLimit);
        StringAssert.Contains(viewModel.WeeklyRateLimit, "残り 65%");
        StringAssert.Contains(viewModel.AllRateLimits, "位置=第1枠");
        StringAssert.Contains(viewModel.AllRateLimits, "分類=週間枠");
        StringAssert.Contains(viewModel.NotificationTarget, "期間=10080分");
        StringAssert.Contains(viewModel.AllRateLimits, "通知設定=有効");
        StringAssert.Contains(viewModel.AllRateLimits, "有効通知=早期警告/通常警告/最終警告/新しい利用期間の開始");
        StringAssert.Contains(viewModel.AllRateLimits, "リセット時刻未取得");
        StringAssert.Contains(viewModel.AllRateLimits, "回復連番=0");
        Assert.IsFalse(viewModel.FiveHourCard.IsObserved);
        Assert.IsTrue(viewModel.WeeklyCard.IsObserved);
        Assert.AreEqual(65D, viewModel.WeeklyCard.RemainingPercent);
    }

    /// <summary>
    /// Unknown枠は表示対象に残しつつ初期状態では通知対象外と表示することを検証します。
    /// </summary>
    [TestMethod]
    public void Initialize_UnknownWindow_ShowsNotificationExcluded()
    {
        RateLimitWindow unknown = new()
        {
            LimitId = "future",
            Position = RateLimitPosition.Secondary,
            Classification = RateLimitClassification.Unknown,
            WindowDurationMinutes = 1440,
            UsedPercent = 10,
            RemainingPercent = 90,
        };
        StatusViewModel viewModel = new();

        viewModel.Initialize(
            AppSettings.CreateDefault(),
            new ApplicationState
            {
                LastUsageSnapshot = new UsageSnapshot
                {
                    CapturedAtUtc = DateTimeOffset.UnixEpoch,
                    RateLimits = [unknown],
                },
            });

        StringAssert.Contains(viewModel.AllRateLimits, "分類=期間不明");
        StringAssert.Contains(viewModel.AllRateLimits, "通知設定=通知対象外");
        StringAssert.Contains(viewModel.NotificationTarget, "通知対象外");
    }

    /// <summary>Gmail通知設定とOAuth認証済みアカウントを別々に表示することを検証します。</summary>
    [TestMethod]
    public async Task RefreshGmailAuthenticationStatusAsync_Authenticated_ShowsSeparateStatusAndAccount()
    {
        StubGmailAuthenticationService authentication = new()
        {
            Status = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Authenticated,
                HasClientConfiguration = true,
                AuthenticatedEmailAddress = "user@example.com",
            },
        };
        StatusViewModel viewModel = new(authentication);
        AppSettings settings = AppSettings.CreateDefault() with { GmailNotificationEnabled = true };
        viewModel.Initialize(settings, new ApplicationState());

        await viewModel.RefreshGmailAuthenticationStatusAsync(CancellationToken.None);

        Assert.AreEqual("有効", viewModel.GmailNotificationStatus);
        Assert.AreEqual("認証済み", viewModel.GmailAuthenticationStatus);
        Assert.AreEqual("user@example.com", viewModel.GmailAuthenticatedAccount);
        Assert.AreEqual("u***@example.com", viewModel.MaskedGmailAccount);
    }

    /// <summary>Gmail通知が無効の場合に認証状態とは別に無効と表示することを検証します。</summary>
    [TestMethod]
    public void Initialize_GmailDisabled_ShowsDisabled()
    {
        StatusViewModel viewModel = new();

        viewModel.Initialize(AppSettings.CreateDefault(), new ApplicationState());

        Assert.AreEqual("未設定（任意）", viewModel.GmailNotificationStatus);
    }

    /// <summary>WindowsとGmailの直近配送結果を互いに混同せず表示することを検証します。</summary>
    [TestMethod]
    public void Initialize_ChannelDeliveryResults_ShowsBothChannelsSeparately()
    {
        StatusViewModel viewModel = new();
        ApplicationState state = new()
        {
            WindowsDeliveryResult = new DeliveryResultState
            {
                Status = DeliveryStatus.Succeeded,
                AttemptedAtUtc = new DateTimeOffset(2026, 8, 10, 1, 0, 0, TimeSpan.Zero),
                Summary = "Windows通知を表示しました。",
            },
            GmailDeliveryResult = new DeliveryResultState
            {
                Status = DeliveryStatus.Failed,
                AttemptedAtUtc = new DateTimeOffset(2026, 8, 10, 2, 0, 0, TimeSpan.Zero),
                Summary = "一時的な送信失敗です。",
            },
        };

        viewModel.Initialize(AppSettings.CreateDefault(), state);

        StringAssert.Contains(viewModel.LastWindowsNotification, "成功");
        StringAssert.Contains(viewModel.LastWindowsNotification, "Windows通知を表示しました。");
        StringAssert.Contains(viewModel.LastGmailNotification, "失敗");
        StringAssert.Contains(viewModel.LastGmailNotification, "一時的な送信失敗です。");
    }

    /// <summary>保存済みの監視障害enum名を状態画面へそのまま表示しないことを検証します。</summary>
    [TestMethod]
    public void Initialize_MonitoringFailureSummary_ShowsJapaneseText()
    {
        StatusViewModel viewModel = new();
        ApplicationState state = new()
        {
            WindowsDeliveryResult = new DeliveryResultState
            {
                Status = DeliveryStatus.Succeeded,
                AttemptedAtUtc = DateTimeOffset.UnixEpoch,
                Summary = nameof(RateLimitNotificationType.MonitoringFailure),
            },
        };

        viewModel.Initialize(AppSettings.CreateDefault(), state);

        StringAssert.Contains(viewModel.LastWindowsNotification, "監視障害通知");
        Assert.IsFalse(viewModel.LastWindowsNotification.Contains("MonitoringFailure", StringComparison.Ordinal));
    }

    /// <summary>Gmailだけが成功した利用枠でもGmail最終通知を表示することを検証します。</summary>
    [TestMethod]
    public void Initialize_GmailOnlyWindowSuccess_ShowsGmailNotificationWithOwnTimestamp()
    {
        RateLimitWindow window = new()
        {
            LimitId = "codex",
            Position = RateLimitPosition.Primary,
            Classification = RateLimitClassification.FiveHour,
            WindowDurationMinutes = 300,
            UsedPercent = 1,
            RemainingPercent = 99,
        };
        ApplicationState state = new()
        {
            LastUsageSnapshot = new UsageSnapshot
            {
                CapturedAtUtc = new DateTimeOffset(2026, 8, 10, 3, 0, 0, TimeSpan.Zero),
                RateLimits = [window],
            },
            RateLimitNotificationStates =
            [
                new RateLimitNotificationState
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = 300,
                    RecoveryWindowId = "window-1",
                    NotificationType = RateLimitNotificationType.ShortWindowRecovered,
                    NotificationStage = RateLimitNotificationStage.Recovered,
                    ConditionMetAtUtc = new DateTimeOffset(2026, 8, 10, 1, 0, 0, TimeSpan.Zero),
                    WindowsDeliveryStatus = DeliveryStatus.NotAttempted,
                    GmailDeliveryStatus = DeliveryStatus.Succeeded,
                    GmailLastAttemptedAtUtc = new DateTimeOffset(2026, 8, 10, 2, 0, 0, TimeSpan.Zero),
                },
            ],
        };
        StatusViewModel viewModel = new();

        viewModel.Initialize(AppSettings.CreateDefault(), state);

        StringAssert.Contains(viewModel.AllRateLimits, "最終Windows通知=なし");
        StringAssert.Contains(viewModel.AllRateLimits, "最終Gmail通知=短期枠回復/回復");
        StringAssert.Contains(viewModel.AllRateLimits, "2026/08/10");
    }

    /// <summary>Gmail再認証必要状態と安全な案内を表示することを検証します。</summary>
    [TestMethod]
    public async Task RefreshGmailAuthenticationStatusAsync_ReauthenticationRequired_ShowsGuidance()
    {
        StubGmailAuthenticationService authentication = new()
        {
            Status = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.ReauthenticationRequired,
                HasClientConfiguration = true,
                LastErrorSummary = "Googleアカウントを再認証してください。",
            },
        };
        StatusViewModel viewModel = new(authentication);
        viewModel.Initialize(AppSettings.CreateDefault(), new ApplicationState());

        await viewModel.RefreshGmailAuthenticationStatusAsync(CancellationToken.None);

        StringAssert.Contains(viewModel.GmailAuthenticationStatus, "再認証が必要");
        Assert.AreEqual("未認証", viewModel.GmailAuthenticatedAccount);
    }

    /// <summary>認証状態の安全性に依存せずトークンやclient_secretを画面へ表示しないことを検証します。</summary>
    [TestMethod]
    public async Task RefreshGmailAuthenticationStatusAsync_DoesNotDisplayCredentialDetails()
    {
        StubGmailAuthenticationService authentication = new()
        {
            Status = new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Error,
                HasClientConfiguration = true,
                LastErrorSummary = "access_token=secret client_secret=secret",
            },
        };
        StatusViewModel viewModel = new(authentication);
        viewModel.Initialize(AppSettings.CreateDefault(), new ApplicationState());

        await viewModel.RefreshGmailAuthenticationStatusAsync(CancellationToken.None);

        Assert.AreEqual("エラー", viewModel.GmailAuthenticationStatus);
        Assert.IsFalse(viewModel.GmailAuthenticationStatus.Contains("access_token", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.GmailAuthenticationStatus.Contains("client_secret", StringComparison.Ordinal));
    }

    /// <summary>5時間枠と週間枠を文字列解析なしで別々のカードへ反映することを検証します。</summary>
    [TestMethod]
    public void SetSnapshot_TwoKnownWindows_PopulatesStructuredCards()
    {
        DateTimeOffset capturedAtUtc = new(2026, 8, 20, 0, 0, 0, TimeSpan.Zero);
        UsageSnapshot snapshot = new()
        {
            CapturedAtUtc = capturedAtUtc,
            RateLimits =
            [
                new RateLimitWindow
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    Classification = RateLimitClassification.FiveHour,
                    WindowDurationMinutes = 300,
                    UsedPercent = 42,
                    RemainingPercent = 58,
                    ResetsAtUtc = capturedAtUtc.AddHours(2),
                },
                new RateLimitWindow
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Secondary,
                    Classification = RateLimitClassification.Weekly,
                    WindowDurationMinutes = 10080,
                    UsedPercent = 35,
                    RemainingPercent = 65,
                    ResetsAtUtc = capturedAtUtc.AddDays(4),
                },
            ],
        };
        StatusViewModel viewModel = new();

        viewModel.SetSnapshot(snapshot, new ApplicationState(), AppSettings.CreateDefault());

        Assert.AreEqual(58D, viewModel.FiveHourCard.RemainingPercent);
        Assert.AreEqual("使用率 42%", viewModel.FiveHourCard.UsedPercentText);
        Assert.AreEqual(65D, viewModel.WeeklyCard.RemainingPercent);
        Assert.AreEqual("週間枠", viewModel.WeeklyCard.ClassificationText);
    }

    /// <summary>正常取得時に監視状態を正常として表示することを検証します。</summary>
    [TestMethod]
    public void SetSnapshot_Success_ShowsHealthyMonitoringState()
    {
        StatusViewModel viewModel = new();

        viewModel.SetSnapshot(
            new UsageSnapshot { CapturedAtUtc = DateTimeOffset.UnixEpoch },
            new ApplicationState(),
            AppSettings.CreateDefault());

        Assert.AreEqual("正常に監視中", viewModel.MonitoringHeadline);
        Assert.AreEqual(DashboardVisualState.Normal, viewModel.MonitoringVisualState);
        StringAssert.Contains(viewModel.MonitoringDetail, "App Server");
    }

    /// <summary>監視失敗時に再接続待ちと安全な理由を表示することを検証します。</summary>
    [TestMethod]
    public void SetFailure_ShowsReconnectState()
    {
        StatusViewModel viewModel = new();

        viewModel.SetFailure(2, "接続を確認しています");

        Assert.AreEqual("再接続待ち", viewModel.MonitoringHeadline);
        Assert.AreEqual("接続を確認しています", viewModel.MonitoringDetail);
        Assert.AreEqual(DashboardVisualState.Danger, viewModel.MonitoringVisualState);
        Assert.AreEqual("2回", viewModel.ConsecutiveFailures);
    }

    /// <summary>取得中状態を監視エラーとは異なる表示にすることを検証します。</summary>
    [TestMethod]
    public void SetChecking_ShowsCheckingState()
    {
        StatusViewModel viewModel = new();

        viewModel.SetChecking();

        Assert.AreEqual("確認中", viewModel.MonitoringHeadline);
        Assert.AreEqual(DashboardVisualState.Checking, viewModel.MonitoringVisualState);
    }

    /// <summary>チャネル別の直近結果を新しい順の通知一覧へ反映することを検証します。</summary>
    [TestMethod]
    public void Initialize_ChannelDeliveryResults_CreatesRecentNotificationItems()
    {
        StatusViewModel viewModel = new();
        ApplicationState state = new()
        {
            WindowsDeliveryResult = new DeliveryResultState
            {
                Status = DeliveryStatus.Succeeded,
                AttemptedAtUtc = new DateTimeOffset(2026, 8, 20, 1, 0, 0, TimeSpan.Zero),
                Summary = "Windows通知",
            },
            GmailDeliveryResult = new DeliveryResultState
            {
                Status = DeliveryStatus.Failed,
                AttemptedAtUtc = new DateTimeOffset(2026, 8, 20, 2, 0, 0, TimeSpan.Zero),
                Summary = "Gmail通知",
            },
        };

        viewModel.Initialize(AppSettings.CreateDefault(), state);

        Assert.IsTrue(viewModel.HasRecentNotifications);
        Assert.AreEqual(2, viewModel.RecentNotifications.Count);
        Assert.AreEqual("Gmail", viewModel.RecentNotifications[0].Channel);
        Assert.AreEqual("失敗", viewModel.RecentNotifications[0].StatusText);
        Assert.AreEqual("Windows", viewModel.RecentNotifications[1].Channel);
        Assert.IsTrue(viewModel.RecentNotifications[1].IsSucceeded);
    }

    /// <summary>Windows通知設定が無効な場合に状態カードへ反映することを検証します。</summary>
    [TestMethod]
    public void Initialize_WindowsNotificationsDisabled_ShowsDisabled()
    {
        StatusViewModel viewModel = new();
        AppSettings settings = AppSettings.CreateDefault() with { WindowsNotificationEnabled = false };

        viewModel.Initialize(settings, new ApplicationState());

        Assert.AreEqual("無効", viewModel.WindowsNotificationStatus);
    }
}
