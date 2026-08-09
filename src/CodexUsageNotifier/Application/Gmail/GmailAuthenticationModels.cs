namespace CodexUsageNotifier.Application.Gmail;

/// <summary>
/// Gmail認証の現在状態を表します。
/// </summary>
public enum GmailAuthenticationState
{
    /// <summary>OAuthクライアント設定がありません。</summary>
    NotConfigured,
    /// <summary>認証情報がありません。</summary>
    Unauthenticated,
    /// <summary>ブラウザー認証を実行中です。</summary>
    Authenticating,
    /// <summary>認証済みです。</summary>
    Authenticated,
    /// <summary>アクセストークンの更新が必要です。</summary>
    RefreshRequired,
    /// <summary>Googleアカウントでの再認証が必要です。</summary>
    ReauthenticationRequired,
    /// <summary>認証状態の読み込みなどでエラーが発生しました。</summary>
    Error,
}

/// <summary>
/// 画面へ公開してよいGmail認証状態を保持します。
/// </summary>
public sealed record GmailAuthenticationStatus
{
    /// <summary>認証状態を取得します。</summary>
    public GmailAuthenticationState State { get; init; }

    /// <summary>認証済みメールアドレスを取得します。</summary>
    public string? AuthenticatedEmailAddress { get; init; }

    /// <summary>最後に認証へ成功したUTC時刻を取得します。</summary>
    public DateTimeOffset? LastAuthenticatedAtUtc { get; init; }

    /// <summary>最後にアクセストークンを更新したUTC時刻を取得します。</summary>
    public DateTimeOffset? LastTokenRefreshedAtUtc { get; init; }

    /// <summary>ユーザー向けの安全な最終エラー概要を取得します。</summary>
    public string? LastErrorSummary { get; init; }

    /// <summary>OAuthクライアント設定が存在するかを取得します。</summary>
    public bool HasClientConfiguration { get; init; }

    /// <summary>Gmail APIでメールを送信できる認証状態かを取得します。</summary>
    public bool CanSendMail => (State is GmailAuthenticationState.Authenticated or GmailAuthenticationState.RefreshRequired)
        && !string.IsNullOrWhiteSpace(AuthenticatedEmailAddress);

    /// <summary>テストメールを送信できる認証状態かを取得します。</summary>
    public bool CanSendTestMail => CanSendMail;

    /// <summary>再認証が必要かを取得します。</summary>
    public bool RequiresReauthentication => State == GmailAuthenticationState.ReauthenticationRequired;
}

/// <summary>
/// OAuthクライアント設定の検証結果を保持します。
/// </summary>
public sealed record GoogleOAuthClientConfigurationStatus
{
    /// <summary>設定ファイルが存在するかを取得します。</summary>
    public bool Exists { get; init; }

    /// <summary>設定ファイルが有効かを取得します。</summary>
    public bool IsValid { get; init; }

    /// <summary>標準配置先を取得します。</summary>
    public required string StandardPath { get; init; }

    /// <summary>ユーザー向けの安全な説明を取得します。</summary>
    public required string Message { get; init; }
}

/// <summary>
/// Gmail操作の成否とユーザー向けメッセージを保持します。
/// </summary>
public sealed record GmailOperationResult
{
    /// <summary>操作に成功したかを取得します。</summary>
    public bool Succeeded { get; init; }

    /// <summary>ユーザーが操作をキャンセルしたかを取得します。</summary>
    public bool WasCanceled { get; init; }

    /// <summary>ローカル認証情報の削除に成功したかを取得します。</summary>
    public bool LocalCredentialsRemoved { get; init; }

    /// <summary>Google側の失効処理に成功したかを取得します。</summary>
    public bool RemoteRevocationSucceeded { get; init; }

    /// <summary>ユーザー向けの安全な結果メッセージを取得します。</summary>
    public required string Message { get; init; }
}

/// <summary>
/// 暗号化して保存する認証メタデータを保持します。
/// </summary>
public sealed record GmailCredentialMetadata
{
    /// <summary>認証済みメールアドレスを取得します。</summary>
    public required string EmailAddress { get; init; }

    /// <summary>最後に認証へ成功したUTC時刻を取得します。</summary>
    public DateTimeOffset LastAuthenticatedAtUtc { get; init; }

    /// <summary>最後にアクセストークンを更新したUTC時刻を取得します。</summary>
    public DateTimeOffset? LastTokenRefreshedAtUtc { get; init; }
}

/// <summary>
/// 認証情報の暗号化、復号、または永続化に失敗したことを表します。
/// </summary>
public sealed class GmailCredentialStoreException : Exception
{
    /// <summary>安全な固定メッセージと内部例外を受け取ります。</summary>
    public GmailCredentialStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// OAuthクライアント設定が存在しないか不正であることを表します。
/// </summary>
public sealed class GoogleOAuthClientConfigurationException : Exception
{
    /// <summary>ユーザー向けにも安全な設定エラーを受け取ります。</summary>
    public GoogleOAuthClientConfigurationException(string message)
        : base(message)
    {
    }

    /// <summary>安全な設定エラーと内部例外を受け取ります。</summary>
    public GoogleOAuthClientConfigurationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Gmail APIエラーの安全な分類を表します。
/// </summary>
public enum GmailApiErrorKind
{
    /// <summary>認証が無効です。</summary>
    Unauthorized,
    /// <summary>権限不足またはAPI未有効です。</summary>
    Forbidden,
    /// <summary>一時的な通信またはサーバーエラーです。</summary>
    Transient,
    /// <summary>分類できないAPIエラーです。</summary>
    Unknown,
}

/// <summary>
/// Gmail API呼び出しの安全に分類された失敗を表します。
/// </summary>
public sealed class GmailApiOperationException : Exception
{
    /// <summary>分類と安全な概要を受け取ります。</summary>
    public GmailApiOperationException(GmailApiErrorKind kind, string message, Exception innerException)
        : base(message, innerException)
    {
        Kind = kind;
    }

    /// <summary>APIエラー分類を取得します。</summary>
    public GmailApiErrorKind Kind { get; }
}
