using CodexUsageNotifier.Application.Maintenance;
using CodexUsageNotifier.Infrastructure.Logging;
using CodexUsageNotifier.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Infrastructure.Logging;

/// <summary>
/// 日付別ログの保持期間と安全な対象限定を検証します。
/// </summary>
[TestClass]
public sealed class LogMaintenanceTests
{
    private static readonly DateTimeOffset CurrentLocalTime =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.FromHours(9));

    /// <summary>保持境界より古い対象ログだけを削除することを検証します。</summary>
    [TestMethod]
    public async Task MaintainAsync_MixedAges_DeletesOnlyExpiredLogs()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        paths.EnsureDirectories();
        string expired = CreateLog(paths, "codex-usage-notifier-20260711.log");
        string boundary = CreateLog(paths, "codex-usage-notifier-20260712.log");
        string recent = CreateLog(paths, "codex-usage-notifier-20260801.log");
        string today = CreateLog(paths, "codex-usage-notifier-20260811.log");

        LogMaintenanceResult result = await CreateMaintenance(paths).MaintainAsync(
            30,
            CurrentLocalTime,
            CancellationToken.None);

        Assert.AreEqual(1, result.DeletedFileCount);
        Assert.IsFalse(File.Exists(expired));
        Assert.IsTrue(File.Exists(boundary));
        Assert.IsTrue(File.Exists(recent));
        Assert.IsTrue(File.Exists(today));
    }

    /// <summary>形式違いと不正日付のログを削除しないことを検証します。</summary>
    [TestMethod]
    public async Task MaintainAsync_UnknownNames_PreservesFiles()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        paths.EnsureDirectories();
        string notes = CreateLog(paths, "my-notes.log");
        string backup = CreateLog(paths, "codex-usage-notifier-backup.log");
        string invalidDate = CreateLog(paths, "codex-usage-notifier-20261340.log");

        LogMaintenanceResult result = await CreateMaintenance(paths).MaintainAsync(
            30,
            CurrentLocalTime,
            CancellationToken.None);

        Assert.AreEqual(0, result.DeletedFileCount);
        Assert.IsTrue(File.Exists(notes));
        Assert.IsTrue(File.Exists(backup));
        Assert.IsTrue(File.Exists(invalidDate));
    }

    /// <summary>保持日数の下限と上限を許容し、範囲外を拒否することを検証します。</summary>
    [TestMethod]
    public async Task MaintainAsync_RetentionBoundaries_ValidatesRange()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        LogMaintenance maintenance = CreateMaintenance(paths);

        await maintenance.MaintainAsync(7, CurrentLocalTime, CancellationToken.None);
        await maintenance.MaintainAsync(3650, CurrentLocalTime, CancellationToken.None);
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => maintenance.MaintainAsync(6, CurrentLocalTime, CancellationToken.None));
        await Assert.ThrowsExceptionAsync<ArgumentOutOfRangeException>(
            () => maintenance.MaintainAsync(3651, CurrentLocalTime, CancellationToken.None));
    }

    /// <summary>キャンセル済み処理がログを削除しないことを検証します。</summary>
    [TestMethod]
    public async Task MaintainAsync_Canceled_PreservesLogs()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        paths.EnsureDirectories();
        string expired = CreateLog(paths, "codex-usage-notifier-20260101.log");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => CreateMaintenance(paths).MaintainAsync(30, CurrentLocalTime, cancellation.Token));

        Assert.IsTrue(File.Exists(expired));
    }

    /// <summary>削除できないログを保持し、失敗件数として返すことを検証します。</summary>
    [TestMethod]
    public async Task MaintainAsync_DeletionFails_ContinuesSafely()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        paths.EnsureDirectories();
        string expired = CreateLog(paths, "codex-usage-notifier-20260101.log");
        await using FileStream lockStream = new(
            expired,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None,
            bufferSize: 1,
            useAsync: true);

        LogMaintenanceResult result = await CreateMaintenance(paths).MaintainAsync(
            30,
            CurrentLocalTime,
            CancellationToken.None);

        Assert.AreEqual(0, result.DeletedFileCount);
        Assert.AreEqual(1, result.FailedFileCount);
        Assert.IsTrue(File.Exists(expired));
    }

    /// <summary>テスト用の対象ログファイルを生成します。</summary>
    private static string CreateLog(AppDataPaths paths, string fileName)
    {
        string path = Path.Combine(paths.LogDirectory, fileName);
        File.WriteAllText(path, "test");
        return path;
    }

    /// <summary>指定パスを使用するログ保守を生成します。</summary>
    private static LogMaintenance CreateMaintenance(AppDataPaths paths)
    {
        return new LogMaintenance(paths, NullLogger<LogMaintenance>.Instance);
    }
}
