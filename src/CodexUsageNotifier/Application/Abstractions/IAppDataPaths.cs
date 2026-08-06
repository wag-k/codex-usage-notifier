namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// アプリケーションデータの保存先を抽象化します。
/// </summary>
public interface IAppDataPaths
{
    /// <summary>
    /// アプリケーションデータのルートディレクトリを取得します。
    /// </summary>
    string RootDirectory { get; }

    /// <summary>
    /// 設定ファイルのパスを取得します。
    /// </summary>
    string SettingsFilePath { get; }

    /// <summary>
    /// 状態ファイルのパスを取得します。
    /// </summary>
    string StateFilePath { get; }

    /// <summary>
    /// 利用履歴ファイルのパスを取得します。
    /// </summary>
    string UsageHistoryFilePath { get; }

    /// <summary>
    /// 認証情報ディレクトリのパスを取得します。
    /// </summary>
    string AuthDirectory { get; }

    /// <summary>
    /// Google OAuthクライアント設定ファイルのパスを取得します。
    /// </summary>
    string GoogleOAuthClientFilePath { get; }

    /// <summary>
    /// DPAPI保護されたGmail認証情報ファイルのパスを取得します。
    /// </summary>
    string GoogleCredentialFilePath { get; }

    /// <summary>
    /// ログディレクトリのパスを取得します。
    /// </summary>
    string LogDirectory { get; }
}
