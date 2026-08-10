using System.Text.Json;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Persistence;

/// <summary>
/// アプリケーション状態をJSONファイルへ保存します。
/// </summary>
public sealed class JsonApplicationStateRepository : IApplicationStateRepository
{
    private readonly IAppDataPaths paths;
    private readonly ILogger<JsonApplicationStateRepository> logger;
    private readonly IApplicationStateMigrator migrator;
    private static readonly Action<ILogger, int, int, Exception?> LogFutureSchemaRejected =
        LoggerMessage.Define<int, int>(
            LogLevel.Error,
            new EventId(2010, "FutureStateSchemaRejected"),
            "現在より新しい状態スキーマを検出したため読み込みを中止します。StoredVersion={StoredVersion}, SupportedVersion={SupportedVersion}");

    /// <summary>
    /// 保存先とロガーを受け取ってリポジトリを初期化します。
    /// </summary>
    /// <param name="paths">アプリケーションデータの保存先です。</param>
    /// <param name="logger">処理結果を記録するロガーです。</param>
    /// <param name="migrator">旧状態を段階的に移行するサービスです。</param>
    public JsonApplicationStateRepository(
        IAppDataPaths paths,
        ILogger<JsonApplicationStateRepository> logger,
        IApplicationStateMigrator migrator)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(migrator);
        this.paths = paths;
        this.logger = logger;
        this.migrator = migrator;
    }

    /// <summary>
    /// 保存済み状態を読み込み、存在しない場合や破損時は初期状態を返します。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>読み込んだ状態です。</returns>
    public async Task<ApplicationState> LoadAsync(CancellationToken cancellationToken)
    {
        bool fileExists = File.Exists(paths.StateFilePath);
        int? storedVersion = fileExists
            ? await ReadSchemaVersionAsync(paths.StateFilePath, cancellationToken)
            : null;
        if (storedVersion > ApplicationState.CurrentSchemaVersion)
        {
            LogFutureSchemaRejected(
                logger,
                storedVersion.Value,
                ApplicationState.CurrentSchemaVersion,
                null);
            throw new UnsupportedFutureStateVersionException(
                storedVersion.Value,
                ApplicationState.CurrentSchemaVersion);
        }

        ApplicationState state = await JsonFileStore.ReadOrDefaultAsync(
            paths.StateFilePath,
            ApplicationState.CreateDefault,
            logger,
            cancellationToken);

        if (!fileExists)
        {
            await SaveAsync(state, cancellationToken);
        }
        else if (storedVersion is not null
            && storedVersion < ApplicationState.CurrentSchemaVersion)
        {
            state = migrator.Migrate(state, storedVersion.Value);
            await SaveAsync(state, cancellationToken);
        }

        return state;
    }

    /// <summary>
    /// 状態を一時ファイルへ書き込んだ後、状態ファイルを置換します。
    /// </summary>
    /// <param name="state">保存する状態です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(state);
        return JsonFileStore.WriteAtomicAsync(paths.StateFilePath, state, cancellationToken);
    }

    /// <summary>
    /// JSONを変更せず、ルートのスキーマバージョンだけを読み取ります。
    /// </summary>
    /// <param name="path">状態JSONのパスです。</param>
    /// <param name="cancellationToken">読み込みのキャンセル通知です。</param>
    /// <returns>明示されたバージョン、未指定時のVersion 1、破損時のnullです。</returns>
    private static async Task<int?> ReadSchemaVersionAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = File.OpenRead(path);
            using JsonDocument document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            return document.RootElement.TryGetProperty("schemaVersion", out JsonElement versionElement)
                && versionElement.TryGetInt32(out int version)
                    ? version
                    : 1;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
