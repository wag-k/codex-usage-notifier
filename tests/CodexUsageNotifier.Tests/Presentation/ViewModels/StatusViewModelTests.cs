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
}
