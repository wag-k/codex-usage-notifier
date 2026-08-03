using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

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

        Assert.AreEqual(99, settings.NotificationThresholdPercent);
        Assert.AreEqual(20, settings.WeeklyWarningThresholdPercent);
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
            NotificationThresholdPercent = 95,
            WeeklyWarningThresholdPercent = 15,
            GmailNotificationEnabled = true,
            GmailRecipient = "user@example.com",
            QuietHoursStart = new TimeOnly(23, 30),
            QuietHoursEnd = new TimeOnly(6, 30),
            FallbackPollingMinutes = 30,
        };

        await repository.SaveAsync(expected, CancellationToken.None);
        AppSettings actual = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(expected.NotificationThresholdPercent, actual.NotificationThresholdPercent);
        Assert.AreEqual(expected.WeeklyWarningThresholdPercent, actual.WeeklyWarningThresholdPercent);
        Assert.AreEqual(expected.GmailNotificationEnabled, actual.GmailNotificationEnabled);
        Assert.AreEqual(expected.GmailRecipient, actual.GmailRecipient);
        Assert.AreEqual(expected.QuietHoursStart, actual.QuietHoursStart);
        Assert.AreEqual(expected.QuietHoursEnd, actual.QuietHoursEnd);
        Assert.AreEqual(expected.FallbackPollingMinutes, actual.FallbackPollingMinutes);
    }

    /// <summary>
    /// 通知閾値が仕様の範囲外なら保存を拒否することを確認します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_InvalidThreshold_ThrowsArgumentException()
    {
        using TemporaryDirectory temporaryDirectory = new();
        JsonSettingsRepository repository = CreateRepository(temporaryDirectory.Path);
        AppSettings invalid = new() { NotificationThresholdPercent = 0 };

        await Assert.ThrowsExceptionAsync<ArgumentException>(
            () => repository.SaveAsync(invalid, CancellationToken.None));
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
