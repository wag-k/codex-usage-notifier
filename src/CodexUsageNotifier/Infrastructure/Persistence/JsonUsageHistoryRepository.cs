using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Maintenance;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Persistence;

/// <summary>
/// 取得単位の全利用枠をJSONL履歴へ追記し、過去の識別組み合わせを保持します。
/// </summary>
public sealed partial class JsonUsageHistoryRepository : IUsageHistoryRepository, IUsageHistoryMaintenance, IDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly IAppDataPaths paths;
    private readonly ILogger<JsonUsageHistoryRepository> logger;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly HashSet<string> observedKeys = new(StringComparer.Ordinal);
    private bool observedKeysLoaded;
    private bool disposed;

    /// <summary>
    /// 履歴保存先とロガーを受け取って初期化します。
    /// </summary>
    /// <param name="paths">利用履歴ファイルの保存先です。</param>
    /// <param name="logger">破損行などの診断を記録するロガーです。</param>
    public JsonUsageHistoryRepository(
        IAppDataPaths paths,
        ILogger<JsonUsageHistoryRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        this.paths = paths;
        this.logger = logger;
    }

    /// <summary>
    /// 取得成功時の全利用枠を1つのJSONL行として追記します。
    /// </summary>
    /// <param name="snapshot">保存する全利用枠スナップショットです。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>初めて観測した識別組み合わせの利用枠です。</returns>
    public async Task<IReadOnlyList<RateLimitObservation>> AppendAsync(
        UsageSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            await EnsureObservedKeysLoadedAsync(cancellationToken);
            UsageHistoryEntry entry = CreateEntry(snapshot);
            List<RateLimitObservation> newlyObserved = entry.RateLimits
                .Where(observation => !observedKeys.Contains(CreateObservationKey(observation)))
                .DistinctBy(CreateObservationKey)
                .ToList();

            string? directory = Path.GetDirectoryName(paths.UsageHistoryFilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("利用履歴の保存先ディレクトリを特定できません。");
            }

            Directory.CreateDirectory(directory);
            string jsonLine = JsonSerializer.Serialize(entry, SerializerOptions) + Environment.NewLine;
            await File.AppendAllTextAsync(
                paths.UsageHistoryFilePath,
                jsonLine,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            foreach (RateLimitObservation observation in entry.RateLimits)
            {
                observedKeys.Add(CreateObservationKey(observation));
            }

            return newlyObserved;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<UsageHistoryMaintenanceResult> MaintainAsync(
        DateTimeOffset retainedFromUtc,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(paths.UsageHistoryFilePath))
            {
                observedKeys.Clear();
                observedKeysLoaded = true;
                return new UsageHistoryMaintenanceResult();
            }

            string? directory = Path.GetDirectoryName(paths.UsageHistoryFilePath);
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("利用履歴の保存先ディレクトリを特定できません。");
            }

            Directory.CreateDirectory(directory);
            string temporaryPath = $"{paths.UsageHistoryFilePath}.{Guid.NewGuid():N}.tmp";
            HashSet<string> retainedObservedKeys = new(StringComparer.Ordinal);
            int deletedLineCount = 0;
            int retainedLineCount = 0;
            int corruptedLineCount = 0;
            int lineNumber = 0;
            try
            {
                await using (FileStream stream = new(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize: 4096,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                await using (StreamWriter writer = new(
                    stream,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    bufferSize: 4096,
                    leaveOpen: true))
                {
                    await foreach (string line in File.ReadLinesAsync(
                        paths.UsageHistoryFilePath,
                        cancellationToken))
                    {
                        lineNumber++;
                        bool retainLine = true;
                        try
                        {
                            UsageHistoryEntry? entry = JsonSerializer.Deserialize<UsageHistoryEntry>(
                                line,
                                SerializerOptions);
                            if (entry?.RateLimits is null)
                            {
                                throw new JsonException("利用履歴行に必要なデータがありません。");
                            }

                            retainLine = entry.CapturedAtUtc >= retainedFromUtc;
                            if (retainLine)
                            {
                                foreach (RateLimitObservation observation in entry.RateLimits)
                                {
                                    retainedObservedKeys.Add(CreateObservationKey(observation));
                                }
                            }
                        }
                        catch (JsonException exception)
                        {
                            corruptedLineCount++;
                            retainLine = true;
                            LogCorruptedHistoryLineRetained(logger, lineNumber, exception);
                        }

                        if (retainLine)
                        {
                            await writer.WriteLineAsync(line.AsMemory(), cancellationToken);
                            retainedLineCount++;
                        }
                        else
                        {
                            deletedLineCount++;
                        }
                    }

                    await writer.FlushAsync(cancellationToken);
                    stream.Flush(flushToDisk: true);
                }

                File.Replace(temporaryPath, paths.UsageHistoryFilePath, destinationBackupFileName: null);
                observedKeys.Clear();
                observedKeys.UnionWith(retainedObservedKeys);
                observedKeysLoaded = true;
                LogHistoryMaintenanceCompleted(
                    logger,
                    deletedLineCount,
                    retainedLineCount,
                    corruptedLineCount,
                    null);
                return new UsageHistoryMaintenanceResult
                {
                    DeletedLineCount = deletedLineCount,
                    RetainedLineCount = retainedLineCount,
                    CorruptedLineCount = corruptedLineCount,
                };
            }
            finally
            {
                if (File.Exists(temporaryPath))
                {
                    File.Delete(temporaryPath);
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 既存履歴から過去に観測した識別組み合わせを一度だけ読み込みます。
    /// </summary>
    /// <param name="cancellationToken">読み込みのキャンセル通知です。</param>
    private async Task EnsureObservedKeysLoadedAsync(CancellationToken cancellationToken)
    {
        if (observedKeysLoaded)
        {
            return;
        }

        if (File.Exists(paths.UsageHistoryFilePath))
        {
            int lineNumber = 0;
            await foreach (string line in File.ReadLinesAsync(paths.UsageHistoryFilePath, cancellationToken))
            {
                lineNumber++;
                try
                {
                    UsageHistoryEntry? entry = JsonSerializer.Deserialize<UsageHistoryEntry>(line, SerializerOptions);
                    if (entry is null)
                    {
                        continue;
                    }

                    foreach (RateLimitObservation observation in entry.RateLimits)
                    {
                        observedKeys.Add(CreateObservationKey(observation));
                    }
                }
                catch (JsonException exception)
                {
                    LogCorruptedHistoryLine(logger, lineNumber, exception);
                }
            }
        }

        observedKeysLoaded = true;
    }

    /// <summary>
    /// 全利用枠スナップショットを永続化用履歴へ変換します。
    /// </summary>
    /// <param name="snapshot">変換するスナップショットです。</param>
    /// <returns>取得単位の履歴です。</returns>
    private static UsageHistoryEntry CreateEntry(UsageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        return new UsageHistoryEntry
        {
            CapturedAtUtc = snapshot.CapturedAtUtc,
            RateLimits = snapshot.RateLimits.Select(window => new RateLimitObservation
            {
                LimitId = window.LimitId,
                Position = window.Position,
                WindowDurationMinutes = window.WindowDurationMinutes,
                UsedPercent = window.UsedPercent,
                ResetsAtUtc = window.ResetsAtUtc,
                Classification = window.Classification,
            }).ToArray(),
        };
    }

    /// <summary>
    /// 新規観測判定に使用する3項目の比較キーを生成します。
    /// </summary>
    /// <param name="observation">比較対象の履歴行です。</param>
    /// <returns>LimitId、Position、WindowDurationMinutesを結合したキーです。</returns>
    private static string CreateObservationKey(RateLimitObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        return string.Join(
            '\u001F',
            observation.LimitId ?? string.Empty,
            observation.Position,
            observation.WindowDurationMinutes?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty);
    }

    /// <summary>
    /// 列挙値を文字列で保存するJSON設定を生成します。
    /// </summary>
    /// <returns>履歴専用のJSON設定です。</returns>
    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    /// <summary>
    /// 履歴アクセスの同期資源を解放します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
    }

    [LoggerMessage(2030, LogLevel.Warning, "利用履歴の破損行を無視しました。LineNumber={LineNumber}")]
    private static partial void LogCorruptedHistoryLine(ILogger logger, int lineNumber, Exception exception);

    /// <summary>保守時に破損行を保持したことを記録します。</summary>
    [LoggerMessage(2031, LogLevel.Warning, "利用履歴の破損行をデータ損失防止のため保持しました。LineNumber={LineNumber}")]
    private static partial void LogCorruptedHistoryLineRetained(ILogger logger, int lineNumber, Exception exception);

    /// <summary>利用履歴保守の完了件数を記録します。</summary>
    [LoggerMessage(2032, LogLevel.Information, "利用履歴保守が完了しました。DeletedLineCount={DeletedLineCount}, RetainedLineCount={RetainedLineCount}, CorruptedLineCount={CorruptedLineCount}")]
    private static partial void LogHistoryMaintenanceCompleted(
        ILogger logger,
        int deletedLineCount,
        int retainedLineCount,
        int corruptedLineCount,
        Exception? exception);
}
