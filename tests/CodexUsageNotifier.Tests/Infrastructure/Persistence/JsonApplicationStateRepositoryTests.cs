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
    /// 通知状態と最終取得値を保存して読み戻せることを確認します。
    /// </summary>
    [TestMethod]
    public async Task SaveAsync_StateWithSnapshot_CanLoadSavedState()
    {
        using TemporaryDirectory temporaryDirectory = new();
        AppDataPaths paths = new(temporaryDirectory.Path);
        JsonApplicationStateRepository repository = CreateRepository(paths);
        DateTimeOffset capturedAtUtc = new(2026, 8, 4, 0, 0, 0, TimeSpan.Zero);
        ApplicationState expected = new()
        {
            LastNotifiedRecoveryWindowId = "window-1",
            LastSuccessfulFetchAtUtc = capturedAtUtc,
            LastUsageSnapshot = new UsageSnapshot
            {
                CapturedAtUtc = capturedAtUtc,
                Primary = new RateLimitWindow
                {
                    UsedPercent = 1,
                    RemainingPercent = 99,
                    ResetsAtUtc = capturedAtUtc.AddHours(5),
                },
                ResetCredits = 2,
                Trigger = UsageCheckTrigger.Startup,
            },
            ConsecutiveFailures = 2,
            FailureNotificationSent = true,
        };

        await repository.SaveAsync(expected, CancellationToken.None);
        ApplicationState actual = await repository.LoadAsync(CancellationToken.None);

        Assert.AreEqual(expected.LastNotifiedRecoveryWindowId, actual.LastNotifiedRecoveryWindowId);
        Assert.AreEqual(expected.LastSuccessfulFetchAtUtc, actual.LastSuccessfulFetchAtUtc);
        Assert.AreEqual(99, actual.LastUsageSnapshot?.Primary?.RemainingPercent);
        Assert.AreEqual(2, actual.LastUsageSnapshot?.ResetCredits);
        Assert.AreEqual(UsageCheckTrigger.Startup, actual.LastUsageSnapshot?.Trigger);
        Assert.AreEqual(2, actual.ConsecutiveFailures);
        Assert.IsTrue(actual.FailureNotificationSent);
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

    /// <summary>
    /// テスト対象の状態リポジトリを生成します。
    /// </summary>
    /// <param name="paths">テスト専用の保存先です。</param>
    /// <returns>状態リポジトリです。</returns>
    private static JsonApplicationStateRepository CreateRepository(AppDataPaths paths)
    {
        return new JsonApplicationStateRepository(
            paths,
            NullLogger<JsonApplicationStateRepository>.Instance);
    }
}
