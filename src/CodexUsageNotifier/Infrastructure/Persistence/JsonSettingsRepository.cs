using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Persistence;

/// <summary>
/// アプリケーション設定をJSONファイルへ保存します。
/// </summary>
public sealed class JsonSettingsRepository : ISettingsRepository
{
    private static readonly Action<ILogger, Exception?> LogInvalidSettings =
        LoggerMessage.Define(LogLevel.Warning, new EventId(2001, "InvalidSettings"), "設定値が不正なため初期設定を使用します。");

    private readonly IAppDataPaths paths;
    private readonly ILogger<JsonSettingsRepository> logger;

    /// <summary>
    /// 保存先とロガーを受け取ってリポジトリを初期化します。
    /// </summary>
    /// <param name="paths">アプリケーションデータの保存先です。</param>
    /// <param name="logger">処理結果を記録するロガーです。</param>
    public JsonSettingsRepository(IAppDataPaths paths, ILogger<JsonSettingsRepository> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        this.paths = paths;
        this.logger = logger;
    }

    /// <summary>
    /// 保存済み設定を読み込み、存在しない場合や不正な場合は初期設定を返します。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>有効な設定です。</returns>
    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        bool fileExists = File.Exists(paths.SettingsFilePath);
        AppSettings settings = await JsonFileStore.ReadOrDefaultAsync(
            paths.SettingsFilePath,
            AppSettings.CreateDefault,
            logger,
            cancellationToken);

        if (settings.IsValid())
        {
            if (!fileExists)
            {
                await SaveAsync(settings, cancellationToken);
            }

            return settings;
        }

        LogInvalidSettings(logger, null);
        return AppSettings.CreateDefault();
    }

    /// <summary>
    /// 設定値を検証し、JSONファイルへ安全に保存します。
    /// </summary>
    /// <param name="settings">保存する設定です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (!settings.IsValid())
        {
            throw new ArgumentException("設定値が有効範囲外です。", nameof(settings));
        }

        return JsonFileStore.WriteAtomicAsync(paths.SettingsFilePath, settings, cancellationToken);
    }
}
