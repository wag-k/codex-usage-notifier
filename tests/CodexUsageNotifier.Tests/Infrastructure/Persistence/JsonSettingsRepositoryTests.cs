using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace CodexUsageNotifier.Tests.Infrastructure.Persistence;

/// <summary>
/// JSON設定リポジトリの初期値、保存、検証を確認します。
/// </summary>
[TestClass]
public sealed class JsonSettingsRepositoryTests
{
    /// <summary>
    /// 設定ファイルがない場合に仕様書どおりの初期値が返ることを確認します。
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_FileDoesNotExist_ReturnsSpecificationDefaults()
    {
        using TemporaryDirectory temporaryDirectory = new();
        JsonSettingsRepository repository = CreateRepository(temporaryDirectory.Path);

        AppSettings settings = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(99, settings.ShortWindowRecoveryThresholdPercent);
        Assert.AreEqual("codex", settings.CodexExecutablePath);
        Assert.AreEqual(0, settings.RateLimitNotifications.Count);
        Assert.IsTrue(settings.ShortWindowRecoveryEnabled);
        Assert.AreEqual(50, settings.LongWindowEarlyWarningThresholdPercent);
        Assert.AreEqual(48, settings.LongWindowEarlyWarningHours);
        Assert.AreEqual(20, settings.LongWindowStandardWarningThresholdPercent);
        Assert.AreEqual(24, settings.LongWindowStandardWarningHours);
        Assert.AreEqual(10, settings.LongWindowFinalWarningThresholdPercent);
        Assert.AreEqual(6, settings.LongWindowFinalWarningHours);
        Assert.IsTrue(settings.LongWindowEarlyWarningEnabled);
        Assert.IsTrue(settings.LongWindowStandardWarningEnabled);
        Assert.IsTrue(settings.LongWindowFinalWarningEnabled);
        Assert.IsTrue(settings.LongWindowResetCompletedEnabled);
        Assert.AreEqual(50, settings.ResetInferenceUsageDropPoints);
        Assert.IsTrue(settings.WindowsNotificationEnabled);
        Assert.IsFalse(settings.GmailNotificationEnabled);
        Assert.AreEqual(new TimeOnly(0, 0), settings.QuietHoursStart);
        Assert.AreEqual(new TimeOnly(7, 0), settings.QuietHoursEnd);
        Assert.AreEqual(60, settings.FallbackPollingMinutes);
        Assert.AreEqual(90, settings.HistoryRetentionDays);
        Assert.AreEqual(30, settings.LogRetentionDays);
        Assert.IsTrue(settings.AutoStartEnabled);
    }

    /// <summary>
    /// 保存した設定を同じ値で読み戻せることを確認します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_ValidSettings_CanLoadSavedValues()
    {
        using TemporaryDirectory temporaryDirectory = new();
        JsonSettingsRepository repository = CreateRepository(temporaryDirectory.Path);
        AppSettings expected = new()
        {
            ShortWindowRecoveryThresholdPercent = 95,
            LongWindowEarlyWarningThresholdPercent = 55,
            LongWindowStandardWarningThresholdPercent = 25,
            LongWindowFinalWarningThresholdPercent = 15,
            ResetInferenceUsageDropPoints = 60,
            GmailNotificationEnabled = true,
            GmailRecipient = "user@example.com",
            QuietHoursStart = new TimeOnly(23, 30),
            QuietHoursEnd = new TimeOnly(6, 30),
            FallbackPollingMinutes = 30,
            CodexExecutablePath = "C:\\Tools\\codex.exe",
            RateLimitNotifications =
            [
                new RateLimitNotificationSetting
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = 10080,
                    LongWindowEarlyWarningEnabled = true,
                    LongWindowResetCompletedEnabled = true,
                },
            ],
        };

        await repository.SaveAsync(expected, CancellationToken.None);
        AppSettings actual = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(expected.ShortWindowRecoveryThresholdPercent, actual.ShortWindowRecoveryThresholdPercent);
        Assert.AreEqual(expected.LongWindowEarlyWarningThresholdPercent, actual.LongWindowEarlyWarningThresholdPercent);
        Assert.AreEqual(expected.LongWindowStandardWarningThresholdPercent, actual.LongWindowStandardWarningThresholdPercent);
        Assert.AreEqual(expected.LongWindowFinalWarningThresholdPercent, actual.LongWindowFinalWarningThresholdPercent);
        Assert.AreEqual(expected.ResetInferenceUsageDropPoints, actual.ResetInferenceUsageDropPoints);
        Assert.AreEqual(expected.GmailNotificationEnabled, actual.GmailNotificationEnabled);
        Assert.AreEqual(expected.GmailRecipient, actual.GmailRecipient);
        Assert.AreEqual(expected.QuietHoursStart, actual.QuietHoursStart);
        Assert.AreEqual(expected.QuietHoursEnd, actual.QuietHoursEnd);
        Assert.AreEqual(expected.FallbackPollingMinutes, actual.FallbackPollingMinutes);
        Assert.AreEqual(expected.CodexExecutablePath, actual.CodexExecutablePath);
        Assert.AreEqual(1, actual.RateLimitNotifications.Count);
        Assert.AreEqual(expected.RateLimitNotifications[0].LimitId, actual.RateLimitNotifications[0].LimitId);
        Assert.IsTrue(actual.RateLimitNotifications[0].LongWindowEarlyWarningEnabled);
        Assert.IsTrue(actual.RateLimitNotifications[0].LongWindowResetCompletedEnabled);
    }

    /// <summary>
    /// 通知閾値が仕様の範囲外なら保存を拒否することを確認します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_InvalidThreshold_ThrowsArgumentException()
    {
        using TemporaryDirectory temporaryDirectory = new();
        JsonSettingsRepository repository = CreateRepository(temporaryDirectory.Path);
        AppSettings invalid = new() { ShortWindowRecoveryThresholdPercent = 0 };

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => repository.SaveAsync(invalid, CancellationToken.None));
    }

    /// <summary>
    /// 未対応のログレベルは設定として保存できないことを確認します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_UnknownLogLevel_ThrowsArgumentException()
    {
        using TemporaryDirectory temporaryDirectory = new();
        JsonSettingsRepository repository = CreateRepository(temporaryDirectory.Path);
        AppSettings invalid = new() { MinimumLogLevel = "Verbose" };

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => repository.SaveAsync(invalid, CancellationToken.None));
    }

    /// <summary>
    /// 同じ利用枠識別値が重複する通知設定を拒否することを確認します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_DuplicateRateLimitSetting_ThrowsArgumentException()
    {
        using TemporaryDirectory temporaryDirectory = new();
        JsonSettingsRepository repository = CreateRepository(temporaryDirectory.Path);
        AppSettings invalid = new()
        {
            RateLimitNotifications =
            [
                new RateLimitNotificationSetting
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = 300,
                },
                new RateLimitNotificationSetting
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = 300,
                },
            ],
        };

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => repository.SaveAsync(invalid, CancellationToken.None));
    }

    /// <summary>
    /// 設定ファイルの使用率低下推定閾値だけが不正な場合、他項目を保ったまま50へ補正することを確認します。
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_InvalidResetInferenceUsageDropPoints_FallsBackOnlyThatValue()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string settingsPath = System.IO.Path.Combine(temporaryDirectory.Path, "settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "schemaVersion": 1,
              "codexExecutablePath": "custom-codex",
              "resetInferenceUsageDropPoints": 0
            }
            """);
        JsonSettingsRepository repository = CreateRepository(temporaryDirectory.Path);

        AppSettings settings = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(50, settings.ResetInferenceUsageDropPoints);
        Assert.AreEqual("custom-codex", settings.CodexExecutablePath);
    }

    /// <summary>不正な履歴・ログ保持日数だけを既定値へ補正することを確認します。</summary>
    [TestMethod]
    public async Task LoadAsync_InvalidRetentionDays_FallsBackOnlyRetentionValues()
    {
        using TemporaryDirectory temporaryDirectory = new();
        string settingsPath = System.IO.Path.Combine(temporaryDirectory.Path, "settings.json");
        await File.WriteAllTextAsync(
            settingsPath,
            """
            {
              "schemaVersion": 1,
              "codexExecutablePath": "custom-codex",
              "historyRetentionDays": 0,
              "logRetentionDays": 100000
            }
            """);
        JsonSettingsRepository repository = CreateRepository(temporaryDirectory.Path);

        AppSettings settings = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(90, settings.HistoryRetentionDays);
        Assert.AreEqual(30, settings.LogRetentionDays);
        Assert.AreEqual("custom-codex", settings.CodexExecutablePath);
    }

    /// <summary>保持日数7～3650だけを永続化可能として扱うことを確認します。</summary>
    [TestMethod]
    public async Task SaveAsync_RetentionBoundaries_ValidatesRange()
    {
        using TemporaryDirectory temporaryDirectory = new();
        JsonSettingsRepository repository = CreateRepository(temporaryDirectory.Path);

        await repository.SaveAsync(
            AppSettings.CreateDefault() with { HistoryRetentionDays = 7, LogRetentionDays = 3650 },
            CancellationToken.None);
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => repository.SaveAsync(
                AppSettings.CreateDefault() with { HistoryRetentionDays = 6 },
                CancellationToken.None));
        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => repository.SaveAsync(
                AppSettings.CreateDefault() with { LogRetentionDays = 3651 },
                CancellationToken.None));
    }

    /// <summary>
    /// 配布用の既定設定JSONがコード上の初期値と一致することを確認します。
    /// </summary>
    [TestMethod]
    public async Task DefaultSettingsFile_MatchesCodeDefaults()
    {
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.default.json");
        await using FileStream stream = File.OpenRead(path);
        AppSettings? fileSettings = await JsonSerializer.DeserializeAsync<AppSettings>(
            stream,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        AppSettings codeSettings = AppSettings.CreateDefault();

        Assert.IsNotNull(fileSettings);
        Assert.AreEqual(codeSettings.SchemaVersion, fileSettings.SchemaVersion);
        Assert.AreEqual(codeSettings.CodexExecutablePath, fileSettings.CodexExecutablePath);
        Assert.AreEqual(codeSettings.RateLimitNotifications.Count, fileSettings.RateLimitNotifications.Count);
        Assert.AreEqual(codeSettings.ShortWindowRecoveryEnabled, fileSettings.ShortWindowRecoveryEnabled);
        Assert.AreEqual(codeSettings.ShortWindowRecoveryThresholdPercent, fileSettings.ShortWindowRecoveryThresholdPercent);
        Assert.AreEqual(codeSettings.LongWindowEarlyWarningThresholdPercent, fileSettings.LongWindowEarlyWarningThresholdPercent);
        Assert.AreEqual(codeSettings.LongWindowEarlyWarningEnabled, fileSettings.LongWindowEarlyWarningEnabled);
        Assert.AreEqual(codeSettings.LongWindowEarlyWarningHours, fileSettings.LongWindowEarlyWarningHours);
        Assert.AreEqual(codeSettings.LongWindowStandardWarningThresholdPercent, fileSettings.LongWindowStandardWarningThresholdPercent);
        Assert.AreEqual(codeSettings.LongWindowStandardWarningEnabled, fileSettings.LongWindowStandardWarningEnabled);
        Assert.AreEqual(codeSettings.LongWindowStandardWarningHours, fileSettings.LongWindowStandardWarningHours);
        Assert.AreEqual(codeSettings.LongWindowFinalWarningThresholdPercent, fileSettings.LongWindowFinalWarningThresholdPercent);
        Assert.AreEqual(codeSettings.LongWindowFinalWarningEnabled, fileSettings.LongWindowFinalWarningEnabled);
        Assert.AreEqual(codeSettings.LongWindowFinalWarningHours, fileSettings.LongWindowFinalWarningHours);
        Assert.AreEqual(codeSettings.LongWindowResetCompletedEnabled, fileSettings.LongWindowResetCompletedEnabled);
        Assert.AreEqual(codeSettings.ResetInferenceUsageDropPoints, fileSettings.ResetInferenceUsageDropPoints);
        Assert.AreEqual(codeSettings.WindowsNotificationEnabled, fileSettings.WindowsNotificationEnabled);
        Assert.AreEqual(codeSettings.GmailNotificationEnabled, fileSettings.GmailNotificationEnabled);
        Assert.AreEqual(codeSettings.GmailRecipient, fileSettings.GmailRecipient);
        Assert.AreEqual(codeSettings.QuietHoursEnabled, fileSettings.QuietHoursEnabled);
        Assert.AreEqual(codeSettings.QuietHoursStart, fileSettings.QuietHoursStart);
        Assert.AreEqual(codeSettings.QuietHoursEnd, fileSettings.QuietHoursEnd);
        Assert.AreEqual(codeSettings.FallbackPollingMinutes, fileSettings.FallbackPollingMinutes);
        Assert.AreEqual(codeSettings.ResetCheckDelaySeconds, fileSettings.ResetCheckDelaySeconds);
        Assert.AreEqual(codeSettings.HistoryRetentionDays, fileSettings.HistoryRetentionDays);
        Assert.AreEqual(codeSettings.LogRetentionDays, fileSettings.LogRetentionDays);
        Assert.AreEqual(codeSettings.AutoStartEnabled, fileSettings.AutoStartEnabled);
        Assert.AreEqual(codeSettings.MinimumLogLevel, fileSettings.MinimumLogLevel);
    }

    /// <summary>
    /// テスト対象の設定リポジトリを生成します。
    /// </summary>
    /// <param name="rootDirectory">テスト専用の保存先です。</param>
    /// <returns>設定リポジトリです。</returns>
    private static JsonSettingsRepository CreateRepository(string rootDirectory)
    {
        return new JsonSettingsRepository(
            new AppDataPaths(rootDirectory),
            NullLogger<JsonSettingsRepository>.Instance);
    }
}
