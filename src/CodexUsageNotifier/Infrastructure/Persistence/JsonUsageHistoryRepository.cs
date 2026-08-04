using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Persistence;

/// <summary>
/// 取得単位の全利用枠をJSONL履歴へ追記し、過去の識別組み合わせを保持します。
/// </summary>
public sealed partial class JsonUsageHistoryRepository : IUsageHistoryRepository, IDisposable
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
}
