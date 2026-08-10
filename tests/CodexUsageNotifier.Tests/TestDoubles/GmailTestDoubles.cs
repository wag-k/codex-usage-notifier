using CodexUsageNotifier.Application.Gmail;
using Google.Apis.Auth.OAuth2;

namespace CodexUsageNotifier.Tests.TestDoubles;

/// <summary>
/// OAuthクライアント設定をメモリ上で返すテスト用サービスです。
/// </summary>
internal sealed class StubGoogleOAuthClientConfigurationService : IGoogleOAuthClientConfigurationService
{
    /// <summary>返却する設定状態を取得または設定します。</summary>
    public GoogleOAuthClientConfigurationStatus Status { get; set; } = new()
    {
        Exists = true,
        IsValid = true,
        StandardPath = "C:\\test\\auth\\google-oauth-client.json",
        Message = "OAuthクライアント設定を読み込み済みです。",
    };

    /// <summary>インポート結果を取得または設定します。</summary>
    public GmailOperationResult ImportResult { get; set; } = new()
    {
        Succeeded = true,
        Message = "OAuthクライアント設定を登録しました。",
    };

    /// <inheritdoc />
    public Task<GoogleOAuthClientConfigurationStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Status);
    }

    /// <inheritdoc />
    public Task<GmailOperationResult> ImportAsync(string sourcePath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(ImportResult);
    }

    /// <inheritdoc />
    public Task<ClientSecrets> LoadAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ClientSecrets { ClientId = "test-client", ClientSecret = "test-secret" });
    }
}

/// <summary>
/// Gmail認証状態と操作結果を制御するテスト用サービスです。
/// </summary>
internal sealed class StubGmailAuthenticationService : IGmailAuthenticationService, IGmailAuthenticationStatusProvider
{
    /// <summary>現在返す認証状態を取得または設定します。</summary>
    public GmailAuthenticationStatus Status { get; set; } = new()
    {
        State = GmailAuthenticationState.Unauthenticated,
        HasClientConfiguration = true,
    };

    /// <summary>認証操作の呼び出し回数を取得します。</summary>
    public int AuthenticateCallCount { get; private set; }

    /// <summary>認証解除の呼び出し回数を取得します。</summary>
    public int DisconnectCallCount { get; private set; }

    /// <summary>状態取得時に発生させる一時例外を取得または設定します。</summary>
    public Exception? StatusException { get; set; }

    /// <inheritdoc />
    public Task<GmailAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return StatusException is null
            ? Task.FromResult(Status)
            : Task.FromException<GmailAuthenticationStatus>(StatusException);
    }

    /// <inheritdoc />
    public Task<GmailOperationResult> AuthenticateAsync(bool forceReauthentication, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AuthenticateCallCount++;
        Status = new GmailAuthenticationStatus
        {
            State = GmailAuthenticationState.Authenticated,
            HasClientConfiguration = true,
            AuthenticatedEmailAddress = "user@example.com",
            LastAuthenticatedAtUtc = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
        };
        return Task.FromResult(new GmailOperationResult { Succeeded = true, Message = "認証成功" });
    }

    /// <inheritdoc />
    public Task<GmailOperationResult> DisconnectAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DisconnectCallCount++;
        Status = new GmailAuthenticationStatus
        {
            State = GmailAuthenticationState.Unauthenticated,
            HasClientConfiguration = true,
        };
        return Task.FromResult(new GmailOperationResult
        {
            Succeeded = true,
            LocalCredentialsRemoved = true,
            RemoteRevocationSucceeded = true,
            Message = "認証解除",
        });
    }

    /// <inheritdoc />
    public Task<UserCredential> GetUsableCredentialAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new NotSupportedException("このテストダブルはGoogle資格情報を生成しません。");
    }

    /// <inheritdoc />
    public void MarkReauthenticationRequired(string safeSummary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(safeSummary);
        Status = Status with
        {
            State = GmailAuthenticationState.ReauthenticationRequired,
            LastErrorSummary = safeSummary,
        };
    }
}

/// <summary>
/// Gmailテスト送信結果を記録するテスト用送信サービスです。
/// </summary>
internal sealed class StubGmailTestMailSender : IGmailTestMailSender
{
    /// <summary>送信回数を取得します。</summary>
    public int SendCallCount { get; private set; }

    /// <summary>最後の送信先を取得します。</summary>
    public string? LastRecipient { get; private set; }

    /// <summary>返却する結果を取得または設定します。</summary>
    public GmailOperationResult Result { get; set; } = new()
    {
        Succeeded = true,
        Message = "テストメールを送信しました。",
    };

    /// <inheritdoc />
    public Task<GmailOperationResult> SendAsync(string recipient, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        cancellationToken.ThrowIfCancellationRequested();
        SendCallCount++;
        LastRecipient = recipient;
        return Task.FromResult(Result);
    }
}

/// <summary>
/// 本番Gmail通知の送信回数、送信先、および生成済みメッセージを記録します。
/// </summary>
internal sealed class StubGmailNotificationSender : IGmailNotificationSender
{
    /// <summary>送信回数を取得します。</summary>
    public int SendCallCount { get; private set; }

    /// <summary>送信先を取得します。</summary>
    public List<string> Recipients { get; } = [];

    /// <summary>送信されたメッセージを取得します。</summary>
    public List<GmailNotificationMessage> Messages { get; } = [];

    /// <summary>送信時に発生させる例外を取得または設定します。</summary>
    public Exception? Exception { get; set; }

    /// <inheritdoc />
    public Task SendAsync(
        string recipient,
        GmailNotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        SendCallCount++;
        Recipients.Add(recipient);
        Messages.Add(message);
        if (Exception is not null)
        {
            throw Exception;
        }

        return Task.CompletedTask;
    }
}
