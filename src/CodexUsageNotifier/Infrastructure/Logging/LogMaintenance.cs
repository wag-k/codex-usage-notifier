using System.Globalization;
using System.Text.RegularExpressions;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Maintenance;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Logging;

/// <summary>
/// 命名規則に一致する日付別ログだけへ保持期間を適用します。
/// </summary>
public sealed partial class LogMaintenance : ILogMaintenance
{
    private const int MinimumRetentionDays = 7;
    private const int MaximumRetentionDays = 3650;
    private readonly IAppDataPaths paths;
    private readonly ILogger<LogMaintenance> logger;

    /// <summary>
    /// ログ保存先と処理結果の記録先を指定して初期化します。
    /// </summary>
    /// <param name="paths">ログディレクトリを提供するパス情報です。</param>
    /// <param name="logger">削除結果と失敗を記録するロガーです。</param>
    public LogMaintenance(IAppDataPaths paths, ILogger<LogMaintenance> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        this.paths = paths;
        this.logger = logger;
    }

    /// <inheritdoc />
    public Task<LogMaintenanceResult> MaintainAsync(
        int retentionDays,
        DateTimeOffset currentLocalTime,
        CancellationToken cancellationToken)
    {
        if (retentionDays is < MinimumRetentionDays or > MaximumRetentionDays)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retentionDays),
                $"ログ保持日数は{MinimumRetentionDays}～{MaximumRetentionDays}日で指定してください。");
        }

        return Task.Run(
            () => MaintainCore(retentionDays, currentLocalTime, cancellationToken),
            cancellationToken);
    }

    /// <summary>対象ログを列挙して古いファイルだけを削除します。</summary>
    private LogMaintenanceResult MaintainCore(
        int retentionDays,
        DateTimeOffset currentLocalTime,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(paths.LogDirectory))
        {
            return new LogMaintenanceResult();
        }

        DateOnly currentDate = DateOnly.FromDateTime(currentLocalTime.Date);
        DateOnly retainedFromDate = currentDate.AddDays(-retentionDays);
        int deletedFileCount = 0;
        int failedFileCount = 0;
        foreach (string filePath in Directory.EnumerateFiles(paths.LogDirectory, "*.log", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string fileName = Path.GetFileName(filePath);
            if (!TryParseLogDate(fileName, out DateOnly logDate)
                || logDate >= retainedFromDate
                || logDate == currentDate
                || logDate == currentDate.AddDays(-1))
            {
                continue;
            }

            try
            {
                File.Delete(filePath);
                deletedFileCount++;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                failedFileCount++;
                LogFileDeletionFailed(logger, fileName, exception);
            }
        }

        LogMaintenanceCompleted(logger, deletedFileCount, failedFileCount, null);
        return new LogMaintenanceResult
        {
            DeletedFileCount = deletedFileCount,
            FailedFileCount = failedFileCount,
        };
    }

    /// <summary>既定のログファイル名から日付を厳密に読み取ります。</summary>
    /// <param name="fileName">確認するファイル名です。</param>
    /// <param name="logDate">読み取れた日付です。</param>
    /// <returns>命名規則と実在日付の両方が有効な場合はtrueです。</returns>
    private static bool TryParseLogDate(string fileName, out DateOnly logDate)
    {
        logDate = default;
        Match match = LogFileNamePattern().Match(fileName);
        return match.Success
            && DateOnly.TryParseExact(
                match.Groups[1].Value,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out logDate);
    }

    /// <summary>削除対象を限定するログファイル名パターンを取得します。</summary>
    [GeneratedRegex(@"^codex-usage-notifier-(\d{8})\.log$", RegexOptions.CultureInvariant)]
    private static partial Regex LogFileNamePattern();

    /// <summary>ログ保守の完了件数を記録します。</summary>
    [LoggerMessage(5200, LogLevel.Information, "ログ保守が完了しました。DeletedFileCount={DeletedFileCount}, FailedFileCount={FailedFileCount}")]
    private static partial void LogMaintenanceCompleted(
        ILogger logger,
        int deletedFileCount,
        int failedFileCount,
        Exception? exception);

    /// <summary>対象ログ1件の削除失敗を記録します。</summary>
    [LoggerMessage(5201, LogLevel.Warning, "保持期間外ログを削除できませんでした。FileName={FileName}")]
    private static partial void LogFileDeletionFailed(
        ILogger logger,
        string fileName,
        Exception exception);
}
