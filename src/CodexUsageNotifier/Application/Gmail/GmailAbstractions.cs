using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Util.Store;

namespace CodexUsageNotifier.Application.Gmail;

/// <summary>
/// OAuthクライアント設定ファイルの検証と標準配置を抽象化します。
/// </summary>
public interface IGoogleOAuthClientConfigurationService
{
    /// <summary>標準配置先の状態を取得します。</summary>
    Task<GoogleOAuthClientConfigurationStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>選択された設定ファイルを検証し、標準配置先へ原子的に保存します。</summary>
    Task<GmailOperationResult> ImportAsync(string sourcePath, CancellationToken cancellationToken);

    /// <summary>検証済みクライアント設定を読み込みます。</summary>
    Task<ClientSecrets> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Googleライブラリ用データストアと認証メタデータ保存を提供します。
/// </summary>
public interface IGmailCredentialStore : IDataStore
{
    /// <summary>認証メタデータを読み込みます。</summary>
    Task<GmailCredentialMetadata?> LoadMetadataAsync(CancellationToken cancellationToken);

    /// <summary>認証メタデータを保存します。</summary>
    Task SaveMetadataAsync(GmailCredentialMetadata metadata, CancellationToken cancellationToken);

    /// <summary>認証情報ファイルが存在するかを取得します。</summary>
    bool Exists { get; }
}

/// <summary>
/// バイト列のユーザー単位暗号化を抽象化します。
/// </summary>
public interface IUserDataProtector
{
    /// <summary>平文バイト列を保護します。</summary>
    byte[] Protect(byte[] plaintext);

    /// <summary>保護済みバイト列を復号します。</summary>
    byte[] Unprotect(byte[] protectedData);
}

/// <summary>
/// Google公式OAuthフローの実行をテスト可能に抽象化します。
/// </summary>
public interface IGoogleOAuthFlow
{
    /// <summary>システムブラウザーとループバック受信で認証します。</summary>
    Task<UserCredential> AuthorizeAsync(ClientSecrets clientSecrets, CancellationToken cancellationToken);

    /// <summary>既存資格情報を使ってGoogleアカウントを再認証します。</summary>
    Task<UserCredential> ReauthorizeAsync(UserCredential credential, CancellationToken cancellationToken);

    /// <summary>保存済みトークンからユーザー資格情報を復元します。</summary>
    Task<UserCredential?> LoadCredentialAsync(ClientSecrets clientSecrets, CancellationToken cancellationToken);

    /// <summary>認証済みアカウントのメールアドレスを取得します。</summary>
    Task<string> GetEmailAddressAsync(UserCredential credential, CancellationToken cancellationToken);

    /// <summary>Google公式クライアントでアクセストークンを更新します。</summary>
    Task<bool> RefreshTokenAsync(UserCredential credential, CancellationToken cancellationToken);

    /// <summary>Google公式クライアントでトークンを失効します。</summary>
    Task<bool> RevokeTokenAsync(UserCredential credential, CancellationToken cancellationToken);
}

/// <summary>
/// Gmail認証と解除を提供します。
/// </summary>
public interface IGmailAuthenticationService
{
    /// <summary>現在の認証状態を読み込みます。</summary>
    Task<GmailAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>ブラウザーを使用して認証します。</summary>
    Task<GmailOperationResult> AuthenticateAsync(bool forceReauthentication, CancellationToken cancellationToken);

    /// <summary>Google側の失効を試みた後、ローカル認証情報を削除します。</summary>
    Task<GmailOperationResult> DisconnectAsync(CancellationToken cancellationToken);

    /// <summary>API呼び出しに使用できる資格情報を取得し、必要なら更新します。</summary>
    Task<UserCredential> GetUsableCredentialAsync(CancellationToken cancellationToken);

    /// <summary>恒久的な認証エラーにより再認証が必要になったことを記録します。</summary>
    void MarkReauthenticationRequired(string safeSummary);
}

/// <summary>
/// Gmail認証状態の読み取り口を提供します。
/// </summary>
public interface IGmailAuthenticationStatusProvider
{
    /// <summary>現在の認証状態を取得します。</summary>
    Task<GmailAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Gmail APIへの生メッセージ送信を抽象化します。
/// </summary>
public interface IGmailApiClient
{
    /// <summary>Base64URL化済みMIMEを認証ユーザーから送信します。</summary>
    Task SendRawMessageAsync(string base64UrlMimeMessage, CancellationToken cancellationToken);
}

/// <summary>
/// Google Gmailサービスの生成とmessages.send実行境界を抽象化します。
/// </summary>
public interface IGoogleGmailMessageGateway
{
    /// <summary>認証資格情報でusers.messages.sendを実行します。</summary>
    Task SendAsync(UserCredential credential, string base64UrlMimeMessage, CancellationToken cancellationToken);
}

/// <summary>
/// テストメールのMIME生成を抽象化します。
/// </summary>
public interface IGmailMimeMessageFactory
{
    /// <summary>From、To、件名、本文を含むBase64URL形式のMIMEを生成します。</summary>
    string CreateBase64UrlMessage(string senderAddress, string recipientAddress, string subject, string body);
}

/// <summary>
/// Gmail APIによるテストメール送信を提供します。
/// </summary>
public interface IGmailTestMailSender
{
    /// <summary>指定送信先へ状態を変更しないテストメールを送信します。</summary>
    Task<GmailOperationResult> SendAsync(string recipient, CancellationToken cancellationToken);
}

/// <summary>
/// 本番の利用枠通知メールをGmail APIへ送信します。
/// </summary>
public interface IGmailNotificationSender
{
    /// <summary>指定送信先へ生成済みの本番通知メールを送信します。</summary>
    Task SendAsync(
        string recipient,
        GmailNotificationMessage message,
        CancellationToken cancellationToken);
}
