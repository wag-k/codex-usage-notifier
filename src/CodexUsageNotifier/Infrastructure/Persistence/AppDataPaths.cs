using CodexUsageNotifier.Application.Abstractions;

namespace CodexUsageNotifier.Infrastructure.Persistence;

/// <summary>
/// アプリケーションが使用するローカルデータの保存先をまとめます。
/// </summary>
public sealed class AppDataPaths : IAppDataPaths
{
    /// <summary>
    /// 指定されたルートディレクトリを使うパス情報を初期化します。
    /// </summary>
    /// <param name="rootDirectory">アプリケーションデータのルートディレクトリです。</param>
    public AppDataPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>
    /// アプリケーションデータのルートディレクトリを取得します。
    /// </summary>
    public string RootDirectory { get; }

    /// <summary>
    /// 単一インスタンス制御用ロックファイルのパスを取得します。
    /// </summary>
    public string InstanceLockFilePath => Path.Combine(RootDirectory, "instance.lock");

    /// <summary>
    /// 設定ファイルのパスを取得します。
    /// </summary>
    public string SettingsFilePath => Path.Combine(RootDirectory, "settings.json");

    /// <summary>
    /// 状態ファイルのパスを取得します。
    /// </summary>
    public string StateFilePath => Path.Combine(RootDirectory, "state.json");

    /// <summary>
    /// 利用履歴ファイルのパスを取得します。
    /// </summary>
    public string UsageHistoryFilePath => Path.Combine(RootDirectory, "usage-history.jsonl");

    /// <summary>
    /// 認証情報ディレクトリのパスを取得します。
    /// </summary>
    public string AuthDirectory => Path.Combine(RootDirectory, "auth");

    /// <summary>
    /// Google OAuthクライアント設定ファイルのパスを取得します。
    /// </summary>
    public string GoogleOAuthClientFilePath => Path.Combine(AuthDirectory, "google-oauth-client.json");

    /// <summary>
    /// DPAPI保護されたGmail認証情報ファイルのパスを取得します。
    /// </summary>
    public string GoogleCredentialFilePath => Path.Combine(AuthDirectory, "google-oauth-credentials.dat");

    /// <summary>
    /// ログディレクトリのパスを取得します。
    /// </summary>
    public string LogDirectory => Path.Combine(RootDirectory, "logs");

    /// <summary>
    /// 仕様書に定義された既定保存先のパス情報を生成します。
    /// </summary>
    /// <returns>既定のパス情報です。</returns>
    public static AppDataPaths CreateDefault()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppDataPaths(Path.Combine(localAppData, "CodexUsageNotifier"));
    }

    /// <summary>
    /// アプリケーションが必要とするディレクトリを作成します。
    /// </summary>
    public void EnsureDirectories()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(AuthDirectory);
        Directory.CreateDirectory(LogDirectory);
    }
}
