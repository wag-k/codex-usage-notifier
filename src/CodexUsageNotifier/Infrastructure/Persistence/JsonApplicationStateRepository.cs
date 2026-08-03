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

    /// <summary>
    /// 保存先とロガーを受け取ってリポジトリを初期化します。
    /// </summary>
    /// <param name="paths">アプリケーションデータの保存先です。</param>
    /// <param name="logger">処理結果を記録するロガーです。</param>
    public JsonApplicationStateRepository(
        IAppDataPaths paths,
        ILogger<JsonApplicationStateRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        this.paths = paths;
        this.logger = logger;
    }

    /// <summary>
    /// 保存済み状態を読み込み、存在しない場合や破損時は初期状態を返します。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>読み込んだ状態です。</returns>
    public async Task<ApplicationState> LoadAsync(CancellationToken cancellationToken)
    {
        bool fileExists = File.Exists(paths.StateFilePath);
        ApplicationState state = await JsonFileStore.ReadOrDefaultAsync(
            paths.StateFilePath,
            ApplicationState.CreateDefault,
            logger,
            cancellationToken);

        if (!fileExists)
        {
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
}
