using System.Text.Json;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Infrastructure.Persistence;

/// <summary>
/// JSON状態リポジトリの読み書きと破損時の動作を確認します。
/// </summary>
[TestClass]
public sealed class JsonApplicationStateRepositoryTests
{
    /// <summary>
    /// 既存の状態ファイルを置換し、最新状態を読み戻せることを確認します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_ExistingState_ReplacesWithLatestState()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        JsonApplicationStateRepository repository = CreateRepository(paths);
        DateTimeOffset capturedAtUtc = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        ApplicationState initial = new()
        {
            GmailProductionDeliveryStartedAtUtc = capturedAtUtc.AddMinutes(-10),
            GmailDeliveryEnabledSinceUtc = capturedAtUtc.AddMinutes(-5),
            GmailDeliveryEnabledLastObserved = true,
            GmailAuthenticationWasUsable = true,
            LastNotifiedRecoveryWindowId = "window-1",
            LastSuccessfulFetchAtUtc = capturedAtUtc,
            LastUsageSnapshot = new UsageSnapshot
            {
                CapturedAtUtc = capturedAtUtc,
                RateLimits =
                [
                    new RateLimitWindow
                    {
                        LimitId = "codex",
                        Position = RateLimitPosition.Primary,
                        Classification = RateLimitClassification.FiveHour,
                        UsedPercent = 1,
                        RemainingPercent = 99,
                        WindowDurationMinutes = 300,
                        ResetsAtUtc = capturedAtUtc.AddHours(5),
                    },
                ],
                ResetCredits = 2,
                Trigger = UsageCheckTrigger.Startup,
            },
            ConsecutiveFailures = 2,
            FailureNotificationSent = true,
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
                    ConditionMetAtUtc = capturedAtUtc,
                    DeliveredAtUtc = capturedAtUtc,
                    WindowsDeliveryStatus = DeliveryStatus.Succeeded,
                    WindowsAttemptCount = 2,
                    WindowsLastAttemptedAtUtc = capturedAtUtc.AddMinutes(-1),
                    WindowsNextRetryAtUtc = capturedAtUtc.AddMinutes(4),
                    GmailDeliveryStatus = DeliveryStatus.Failed,
                    GmailAttemptCount = 1,
                    GmailLastAttemptedAtUtc = capturedAtUtc.AddMinutes(-2),
                    GmailNextRetryAtUtc = capturedAtUtc.AddMinutes(3),
                    GmailFailureKind = GmailDeliveryFailureKind.Transient,
                },
            ],
            RateLimitRecoveryStates =
            [
                new RateLimitRecoveryState
                {
                    LimitId = "codex",
                    Position = RateLimitPosition.Primary,
                    WindowDurationMinutes = 300,
                    HasObservation = true,
                    RecoverySequence = 3,
                    LastRemainingPercent = 99,
                },
            ],
        };

        await repository.SaveAsync(initial, CancellationToken.None);
        ApplicationState expected = initial with
        {
            LastNotifiedRecoveryWindowId = "window-2",
            ConsecutiveFailures = 3,
            FailureNotificationSent = false,
        };
        await repository.SaveAsync(expected, CancellationToken.None);
        ApplicationState actual = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(expected.LastNotifiedRecoveryWindowId, actual.LastNotifiedRecoveryWindowId);
        Assert.AreEqual(
            expected.GmailProductionDeliveryStartedAtUtc,
            actual.GmailProductionDeliveryStartedAtUtc);
        Assert.AreEqual(expected.GmailDeliveryEnabledSinceUtc, actual.GmailDeliveryEnabledSinceUtc);
        Assert.IsTrue(actual.GmailDeliveryEnabledLastObserved);
        Assert.IsTrue(actual.GmailAuthenticationWasUsable);
        Assert.AreEqual(expected.LastSuccessfulFetchAtUtc, actual.LastSuccessfulFetchAtUtc);
        Assert.AreEqual(99, actual.LastUsageSnapshot?.RateLimits.Single().RemainingPercent);
        Assert.AreEqual(2, actual.LastUsageSnapshot?.ResetCredits);
        Assert.AreEqual(UsageCheckTrigger.Startup, actual.LastUsageSnapshot?.Trigger);
        Assert.AreEqual(3, actual.ConsecutiveFailures);
        Assert.IsFalse(actual.FailureNotificationSent);
        Assert.AreEqual(
            RateLimitNotificationType.ShortWindowRecovered,
            actual.RateLimitNotificationStates.Single().NotificationType);
        Assert.AreEqual(2, actual.RateLimitNotificationStates.Single().WindowsAttemptCount);
        Assert.AreEqual(
            capturedAtUtc.AddMinutes(-1),
            actual.RateLimitNotificationStates.Single().WindowsLastAttemptedAtUtc);
        Assert.AreEqual(
            capturedAtUtc.AddMinutes(4),
            actual.RateLimitNotificationStates.Single().WindowsNextRetryAtUtc);
        Assert.AreEqual(DeliveryStatus.Failed, actual.RateLimitNotificationStates.Single().GmailDeliveryStatus);
        Assert.AreEqual(1, actual.RateLimitNotificationStates.Single().GmailAttemptCount);
        Assert.AreEqual(
            capturedAtUtc.AddMinutes(-2),
            actual.RateLimitNotificationStates.Single().GmailLastAttemptedAtUtc);
        Assert.AreEqual(
            capturedAtUtc.AddMinutes(3),
            actual.RateLimitNotificationStates.Single().GmailNextRetryAtUtc);
        Assert.AreEqual(
            GmailDeliveryFailureKind.Transient,
            actual.RateLimitNotificationStates.Single().GmailFailureKind);
        Assert.AreEqual(3, actual.RateLimitRecoveryStates.Single().RecoverySequence);
        Assert.IsFalse(Directory.EnumerateFiles(temporaryDirectory.Path, "*.tmp").Any());
    }

    /// <summary>
    /// 状態ファイルが破損していても例外終了せず初期状態を返すことを確認します。
    /// </summary>
    [TestMethod]
    public async Task LoadAsync_CorruptedJson_ReturnsDefaultState()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(paths.StateFilePath, "{ invalid json", CancellationToken.None);
        JsonApplicationStateRepository repository = CreateRepository(paths);

        ApplicationState state = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(ApplicationState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.IsNull(state.LastNotifiedRecoveryWindowId);
        Assert.AreEqual(0, state.ConsecutiveFailures);
    }

    /// <summary>現在スキーマの状態を変更せず通常読み込みできることを確認します。</summary>
    [TestMethod]
    public async Task LoadAsync_CurrentSchema_LoadsWithoutRewrite()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        string original = JsonSerializer.Serialize(
            new ApplicationState { ConsecutiveFailures = 7 },
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        await File.WriteAllTextAsync(paths.StateFilePath, original, CancellationToken.None);
        JsonApplicationStateRepository repository = CreateRepository(paths);

        ApplicationState state = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(ApplicationState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.AreEqual(7, state.ConsecutiveFailures);
        Assert.AreEqual(original, await File.ReadAllTextAsync(paths.StateFilePath, CancellationToken.None));
    }

    /// <summary>サポート済みVersion 2を明示的な段階移行でVersion 3へ保存することを確認します。</summary>
    [TestMethod]
    public async Task LoadAsync_SupportedOldSchema_MigratesToCurrent()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(
            paths.StateFilePath,
            """
            {
              "schemaVersion": 2,
              "consecutiveFailures": 4,
              "gmailProductionDeliveryStartedAtUtc": "2026-08-01T00:00:00+00:00"
            }
            """,
            CancellationToken.None);
        JsonApplicationStateRepository repository = CreateRepository(paths);

        ApplicationState state = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(ApplicationState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.AreEqual(4, state.ConsecutiveFailures);
        Assert.IsNull(state.GmailDeliveryEnabledSinceUtc);
        StringAssert.Contains(
            await File.ReadAllTextAsync(paths.StateFilePath, CancellationToken.None),
            $"\"schemaVersion\": {ApplicationState.CurrentSchemaVersion}");
    }

    /// <summary>Version 1からVersion 2を経由して現在スキーマへ移行できることを確認します。</summary>
    [TestMethod]
    public void Migrate_Version1_UsesSupportedMigrationChain()
    {
        ApplicationStateMigrator migrator = new();

        ApplicationState migrated = migrator.Migrate(
            new ApplicationState { SchemaVersion = 1, ConsecutiveFailures = 2 },
            1);

        Assert.AreEqual(ApplicationState.CurrentSchemaVersion, migrated.SchemaVersion);
        Assert.AreEqual(2, migrated.ConsecutiveFailures);
        Assert.IsNotNull(migrated.RateLimitNotificationStates);
        Assert.IsNotNull(migrated.RateLimitRecoveryStates);
    }

    /// <summary>将来スキーマを拒否し、内容・更新時刻・配置を完全に維持することを確認します。</summary>
    [TestMethod]
    public async Task LoadAsync_FutureSchema_RejectsWithoutChangingFile()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        string original = $$"""
            {
              "schemaVersion": {{ApplicationState.CurrentSchemaVersion + 1}},
              "futureValue": "preserve-exactly"
            }
            """;
        await File.WriteAllTextAsync(paths.StateFilePath, original, CancellationToken.None);
        DateTime fixedWriteTimeUtc = new(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(paths.StateFilePath, fixedWriteTimeUtc);
        byte[] originalBytes = await File.ReadAllBytesAsync(paths.StateFilePath, CancellationToken.None);
        JsonApplicationStateRepository repository = CreateRepository(paths);

        UnsupportedFutureStateVersionException exception =
            await Assert.ThrowsExceptionAsync<UnsupportedFutureStateVersionException>(
                () => repository.LoadAsync(CancellationToken.None));

        CollectionAssert.AreEqual(
            originalBytes,
            await File.ReadAllBytesAsync(paths.StateFilePath, CancellationToken.None));
        Assert.AreEqual(fixedWriteTimeUtc, File.GetLastWriteTimeUtc(paths.StateFilePath));
        Assert.AreEqual(ApplicationState.CurrentSchemaVersion + 1, exception.StoredVersion);
        Assert.AreEqual(1, Directory.EnumerateFiles(paths.RootDirectory).Count());
        Assert.IsFalse(Directory.EnumerateFiles(paths.RootDirectory).Any(path => path.EndsWith(".bak", StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>将来スキーマ拒否時は後続の監視初期化へ進まないことを確認します。</summary>
    [TestMethod]
    public async Task LoadAsync_FutureSchema_DoesNotContinueToMonitoringInitialization()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        Directory.CreateDirectory(paths.RootDirectory);
        await File.WriteAllTextAsync(
            paths.StateFilePath,
            $$"""
            { "schemaVersion": {{ApplicationState.CurrentSchemaVersion + 1}} }
            """,
            CancellationToken.None);
        JsonApplicationStateRepository repository = CreateRepository(paths);
        bool monitoringInitialized = false;

        try
        {
            _ = await repository.LoadAsync(CancellationToken.None);
            monitoringInitialized = true;
        }
        catch (UnsupportedFutureStateVersionException)
        {
            // 起動処理はこの例外を表示して終了し、後続初期化へ進みません。
        }

        Assert.IsFalse(monitoringInitialized);
    }

    /// <summary>将来スキーマ拒否のユーザー向けメッセージが安全で明確であることを確認します。</summary>
    [TestMethod]
    public void CreateUserMessage_FutureSchema_ContainsVersionsAndRecoveryGuidance()
    {
        string message = UnsupportedFutureStateVersionException.CreateUserMessage(4, 3);

        StringAssert.Contains(message, "保存データのバージョン: 4");
        StringAssert.Contains(message, "このアプリが対応するバージョン: 3");
        StringAssert.Contains(message, "起動を中止しました");
        StringAssert.Contains(message, "新しいバージョン");
    }

    /// <summary>
    /// テスト対象の状態リポジトリを生成します。
    /// </summary>
    /// <param name="paths">テスト専用の保存先です。</param>
    /// <returns>状態リポジトリです。</returns>
    private static JsonApplicationStateRepository CreateRepository(AppDataPaths paths)
    {
        return new JsonApplicationStateRepository(
            paths,
            NullLogger<JsonApplicationStateRepository>.Instance,
            new ApplicationStateMigrator());
    }
}
