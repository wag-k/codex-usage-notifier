using CodexUsageNotifier.Application.Gmail;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Oauth2.v2;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// Google公式クライアントでPKCE、システムブラウザー、ループバックOAuthを実行します。
/// </summary>
public sealed class GoogleOAuthFlow : IGoogleOAuthFlow
{
    /// <summary>Gmail送信と認証メールアドレス取得に必要な最小スコープです。</summary>
    public static readonly string[] Scopes =
    [
        "https://www.googleapis.com/auth/gmail.send",
        "openid",
        "email",
    ];

    /// <summary>Googleデータストア内で使用する固定ユーザーキーです。</summary>
    public const string UserKey = "CodexUsageNotifier.Gmail";

    private readonly IDataStore dataStore;

    /// <summary>DPAPI保護されたGoogleデータストアを受け取ります。</summary>
    public GoogleOAuthFlow(IGmailCredentialStore dataStore)
    {
        ArgumentNullException.ThrowIfNull(dataStore);
        this.dataStore = dataStore;
    }

    /// <inheritdoc />
    public Task<UserCredential> AuthorizeAsync(ClientSecrets clientSecrets, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientSecrets);
        GoogleAuthorizationCodeFlow.Initializer initializer = CreateInitializer(clientSecrets);
        LocalServerCodeReceiver receiver = CreateCodeReceiver();
        return GoogleWebAuthorizationBroker.AuthorizeAsync(
            initializer,
            Scopes,
            UserKey,
            usePkce: true,
            cancellationToken,
            dataStore,
            receiver);
    }

    /// <inheritdoc />
    public async Task<UserCredential> ReauthorizeAsync(
        UserCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        LocalServerCodeReceiver receiver = CreateCodeReceiver();
        await GoogleWebAuthorizationBroker.ReauthorizeAsync(
            credential,
            cancellationToken,
            receiver).ConfigureAwait(false);
        return credential;
    }

    /// <inheritdoc />
    public async Task<UserCredential?> LoadCredentialAsync(
        ClientSecrets clientSecrets,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(clientSecrets);
        PkceGoogleAuthorizationCodeFlow flow = new(CreateInitializer(clientSecrets));
        Google.Apis.Auth.OAuth2.Responses.TokenResponse? token =
            await flow.LoadTokenAsync(UserKey, cancellationToken).ConfigureAwait(false);
        return token is null ? null : new UserCredential(flow, UserKey, token);
    }

    /// <inheritdoc />
    public async Task<string> GetEmailAddressAsync(
        UserCredential credential,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        using Oauth2Service service = new(new BaseClientService.Initializer
        {
            ApplicationName = "Codex Usage Notifier",
            HttpClientInitializer = credential,
        });
        Google.Apis.Oauth2.v2.Data.Userinfo profile =
            await service.Userinfo.Get().ExecuteAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(profile.Email))
        {
            throw new InvalidOperationException("認証したGoogleアカウントのメールアドレスを取得できませんでした。");
        }

        return profile.Email;
    }

    /// <inheritdoc />
    public Task<bool> RefreshTokenAsync(UserCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.RefreshTokenAsync(cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> RevokeTokenAsync(UserCredential credential, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.RevokeTokenAsync(cancellationToken);
    }

    /// <summary>Google認証コードフローの共通初期値を生成します。</summary>
    private GoogleAuthorizationCodeFlow.Initializer CreateInitializer(ClientSecrets clientSecrets)
    {
        return new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = clientSecrets,
            Scopes = Scopes,
            DataStore = dataStore,
        };
    }

    /// <summary>ループバックIPを強制するローカル認証コード受信器を生成します。</summary>
    private static LocalServerCodeReceiver CreateCodeReceiver()
    {
        return new LocalServerCodeReceiver(
            "<html><head><meta charset=\"utf-8\"></head><body>認証が完了しました。この画面を閉じてCodex Usage Notifierへ戻ってください。</body></html>",
            LocalServerCodeReceiver.CallbackUriChooserStrategy.ForceLoopbackIp);
    }
}
