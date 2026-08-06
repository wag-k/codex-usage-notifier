using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Infrastructure.Gmail;
using CodexUsageNotifier.Tests.TestDoubles;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Tests.Infrastructure.Gmail;

/// <summary>
/// Gmail認証サービスの状態遷移、更新、解除、同時実行、および安全なログを検証します。
/// </summary>
[TestClass]
public sealed class GmailAuthenticationServiceTests
{
    /// <summary>OAuth設定がない場合にNotConfiguredになることを検証します。</summary>
    [TestMethod]
    public async Task GetStatusAsync_MissingConfiguration_ReturnsNotConfigured()
    {
        StubGoogleOAuthClientConfigurationService configuration = new()
        {
            Status = new GoogleOAuthClientConfigurationStatus
            {
                StandardPath = "C:\\test\\google-oauth-client.json",
                Message = "設定がありません。",
            },
        };
        using GmailAuthenticationService service = CreateService(configuration, new InMemoryCredentialStore(), new StubOAuthFlow());

        GmailAuthenticationStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual(GmailAuthenticationState.NotConfigured, status.State);
    }

    /// <summary>認証成功時にメールアドレスと認証時刻を保持してAuthenticatedになることを検証します。</summary>
    [TestMethod]
    public async Task AuthenticateAsync_Success_TransitionsToAuthenticated()
    {
        InMemoryCredentialStore store = new();
        StubOAuthFlow flow = new() { Credential = CreateCredential(expired: false), EmailAddress = "user@example.com" };
        using GmailAuthenticationService service = CreateService(new(), store, flow);

        GmailOperationResult result = await service.AuthenticateAsync(false, CancellationToken.None);
        GmailAuthenticationStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(GmailAuthenticationState.Authenticated, status.State);
        Assert.AreEqual("user@example.com", status.AuthenticatedEmailAddress);
        Assert.IsNotNull(await store.LoadMetadataAsync(CancellationToken.None));
    }

    /// <summary>ユーザーキャンセル時に未認証状態を維持することを検証します。</summary>
    [TestMethod]
    public async Task AuthenticateAsync_UserCancellation_RemainsUnauthenticated()
    {
        StubOAuthFlow flow = new() { WaitForCancellation = true };
        using GmailAuthenticationService service = CreateService(new(), new InMemoryCredentialStore(), flow);
        using CancellationTokenSource cancellation = new();
        Task<GmailOperationResult> operation = service.AuthenticateAsync(false, cancellation.Token);
        await flow.Started.Task;
        cancellation.Cancel();

        GmailOperationResult result = await operation;
        GmailAuthenticationStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.IsTrue(result.WasCanceled);
        Assert.AreEqual(GmailAuthenticationState.Unauthenticated, status.State);
    }

    /// <summary>同時に開始された2件目のOAuth操作を拒否することを検証します。</summary>
    [TestMethod]
    public async Task AuthenticateAsync_ConcurrentCalls_AllowsSingleFlow()
    {
        StubOAuthFlow flow = new() { WaitForRelease = true, Credential = CreateCredential(expired: false) };
        using GmailAuthenticationService service = CreateService(new(), new InMemoryCredentialStore(), flow);
        Task<GmailOperationResult> first = service.AuthenticateAsync(false, CancellationToken.None);
        await flow.Started.Task;

        GmailOperationResult second = await service.AuthenticateAsync(false, CancellationToken.None);
        flow.Release.TrySetResult();
        GmailOperationResult firstResult = await first;

        Assert.IsTrue(firstResult.Succeeded);
        Assert.IsFalse(second.Succeeded);
        StringAssert.Contains(second.Message, "実行中");
        Assert.AreEqual(1, flow.AuthorizeCallCount);
    }

    /// <summary>既存資格情報がある再認証では公式再認証境界を使用することを検証します。</summary>
    [TestMethod]
    public async Task AuthenticateAsync_ForceWithLoadedCredential_UsesReauthorizationFlow()
    {
        InMemoryCredentialStore store = CreateAuthenticatedStore();
        StubOAuthFlow flow = new() { Credential = CreateCredential(expired: false) };
        using GmailAuthenticationService service = CreateService(new(), store, flow);
        await service.GetStatusAsync(CancellationToken.None);

        GmailOperationResult result = await service.AuthenticateAsync(true, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, flow.ReauthorizeCallCount);
        Assert.AreEqual(0, flow.AuthorizeCallCount);
    }

    /// <summary>破損した認証情報をReauthenticationRequiredとして扱うことを検証します。</summary>
    [TestMethod]
    public async Task GetStatusAsync_CorruptedCredential_RequiresReauthentication()
    {
        InMemoryCredentialStore store = new() { ExistsValue = true, ThrowOnLoad = true };
        using GmailAuthenticationService service = CreateService(new(), store, new StubOAuthFlow());

        GmailAuthenticationStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual(GmailAuthenticationState.ReauthenticationRequired, status.State);
        Assert.IsTrue(status.RequiresReauthentication);
    }

    /// <summary>invalid_grantで再認証必要状態へ移行することを検証します。</summary>
    [TestMethod]
    public async Task GetUsableCredentialAsync_InvalidGrant_RequiresReauthentication()
    {
        InMemoryCredentialStore store = CreateAuthenticatedStore();
        StubOAuthFlow flow = new()
        {
            Credential = CreateCredential(expired: true),
            RefreshException = new TokenResponseException(new TokenErrorResponse { Error = "invalid_grant" }),
        };
        using GmailAuthenticationService service = CreateService(new(), store, flow);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(
            () => service.GetUsableCredentialAsync(CancellationToken.None));
        GmailAuthenticationStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.AreEqual(GmailAuthenticationState.ReauthenticationRequired, status.State);
    }

    /// <summary>一時的な更新通信エラーで認証情報を削除しないことを検証します。</summary>
    [TestMethod]
    public async Task GetUsableCredentialAsync_TransientFailure_KeepsCredential()
    {
        InMemoryCredentialStore store = CreateAuthenticatedStore();
        StubOAuthFlow flow = new()
        {
            Credential = CreateCredential(expired: true),
            RefreshException = new HttpRequestException("temporary"),
        };
        using GmailAuthenticationService service = CreateService(new(), store, flow);

        await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => service.GetUsableCredentialAsync(CancellationToken.None));

        Assert.IsTrue(store.Exists);
        Assert.AreEqual(0, store.ClearCount);
    }

    /// <summary>期限切れアクセストークンを更新し、最終更新時刻を保存することを検証します。</summary>
    [TestMethod]
    public async Task GetUsableCredentialAsync_ExpiredToken_RefreshesAndPersistsMetadata()
    {
        InMemoryCredentialStore store = CreateAuthenticatedStore();
        StubOAuthFlow flow = new() { Credential = CreateCredential(expired: true) };
        using GmailAuthenticationService service = CreateService(new(), store, flow);

        UserCredential credential = await service.GetUsableCredentialAsync(CancellationToken.None);
        GmailAuthenticationStatus status = await service.GetStatusAsync(CancellationToken.None);

        Assert.AreSame(flow.Credential, credential);
        Assert.AreEqual(1, flow.RefreshCallCount);
        Assert.IsNotNull(store.Metadata?.LastTokenRefreshedAtUtc);
        Assert.AreEqual(GmailAuthenticationState.Authenticated, status.State);
    }

    /// <summary>認証解除時にGoogle側失効とローカル削除を実行することを検証します。</summary>
    [TestMethod]
    public async Task DisconnectAsync_Authenticated_RemovesLocalCredential()
    {
        InMemoryCredentialStore store = CreateAuthenticatedStore();
        StubOAuthFlow flow = new() { Credential = CreateCredential(expired: false), RevokeResult = true };
        using GmailAuthenticationService service = CreateService(new(), store, flow);
        await service.GetStatusAsync(CancellationToken.None);

        GmailOperationResult result = await service.DisconnectAsync(CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(result.RemoteRevocationSucceeded);
        Assert.IsTrue(result.LocalCredentialsRemoved);
        Assert.IsFalse(store.Exists);
    }

    /// <summary>Google側の失効に失敗しても明示操作ではローカル認証情報を削除することを検証します。</summary>
    [TestMethod]
    public async Task DisconnectAsync_RemoteRevocationFailure_StillRemovesLocalCredential()
    {
        InMemoryCredentialStore store = CreateAuthenticatedStore();
        StubOAuthFlow flow = new()
        {
            Credential = CreateCredential(expired: false),
            RevokeException = new HttpRequestException("temporary"),
        };
        using GmailAuthenticationService service = CreateService(new(), store, flow);
        await service.GetStatusAsync(CancellationToken.None);

        GmailOperationResult result = await service.DisconnectAsync(CancellationToken.None);

        Assert.IsTrue(result.LocalCredentialsRemoved);
        Assert.IsFalse(result.RemoteRevocationSucceeded);
        Assert.IsFalse(store.Exists);
    }

    /// <summary>内部例外に含まれるトークンやclient secret相当値をログへ出さないことを検証します。</summary>
    [TestMethod]
    public async Task AuthenticateAsync_SensitiveException_DoesNotLogSecretValues()
    {
        const string accessMarker = "sensitive-access-value";
        const string secretMarker = "sensitive-client-value";
        CollectingLogger<GmailAuthenticationService> logger = new();
        StubOAuthFlow flow = new()
        {
            AuthorizeException = new InvalidOperationException(accessMarker + " " + secretMarker),
        };
        using GmailAuthenticationService service = CreateService(new(), new InMemoryCredentialStore(), flow, logger);

        GmailOperationResult result = await service.AuthenticateAsync(false, CancellationToken.None);
        string log = string.Join(Environment.NewLine, logger.Messages);

        Assert.IsFalse(result.Message.Contains(accessMarker, StringComparison.Ordinal));
        Assert.IsFalse(log.Contains(accessMarker, StringComparison.Ordinal));
        Assert.IsFalse(log.Contains(secretMarker, StringComparison.Ordinal));
    }

    /// <summary>認証サービスを生成します。</summary>
    private static GmailAuthenticationService CreateService(
        StubGoogleOAuthClientConfigurationService configuration,
        InMemoryCredentialStore store,
        StubOAuthFlow flow,
        ILogger<GmailAuthenticationService>? logger = null)
    {
        return new GmailAuthenticationService(
            configuration,
            store,
            flow,
            new FixedTimeProvider(new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero)),
            logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<GmailAuthenticationService>.Instance);
    }

    /// <summary>認証済みメタデータを含むストアを生成します。</summary>
    private static InMemoryCredentialStore CreateAuthenticatedStore()
    {
        return new InMemoryCredentialStore
        {
            ExistsValue = true,
            Metadata = new GmailCredentialMetadata
            {
                EmailAddress = "user@example.com",
                LastAuthenticatedAtUtc = new DateTimeOffset(2026, 8, 5, 0, 0, 0, TimeSpan.Zero),
            },
        };
    }

    /// <summary>有効期限を制御したGoogle資格情報を生成します。</summary>
    private static UserCredential CreateCredential(bool expired)
    {
        GoogleAuthorizationCodeFlow flow = new(new GoogleAuthorizationCodeFlow.Initializer
        {
            ClientSecrets = new ClientSecrets { ClientId = "test-client", ClientSecret = "test-value" },
            Scopes = GoogleOAuthFlow.Scopes,
        });
        TokenResponse token = new()
        {
            AccessToken = "test-access",
            RefreshToken = "test-refresh",
            ExpiresInSeconds = 3600,
            IssuedUtc = expired ? DateTime.UtcNow.AddHours(-2) : DateTime.UtcNow,
        };
        return new UserCredential(flow, GoogleOAuthFlow.UserKey, token);
    }

    /// <summary>認証情報をメモリ上で管理するテスト用ストアです。</summary>
    private sealed class InMemoryCredentialStore : IGmailCredentialStore
    {
        private readonly Dictionary<string, object> entries = new(StringComparer.Ordinal);

        /// <summary>存在状態を取得または設定します。</summary>
        public bool ExistsValue { get; set; }

        /// <inheritdoc />
        public bool Exists => ExistsValue || entries.Count != 0 || Metadata is not null;

        /// <summary>認証メタデータを取得または設定します。</summary>
        public GmailCredentialMetadata? Metadata { get; set; }

        /// <summary>読み込み時に破損例外を発生させるかを取得または設定します。</summary>
        public bool ThrowOnLoad { get; set; }

        /// <summary>全削除回数を取得します。</summary>
        public int ClearCount { get; private set; }

        /// <inheritdoc />
        public Task StoreAsync<T>(string key, T value)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            ArgumentNullException.ThrowIfNull(value);
            entries[key] = value;
            ExistsValue = true;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task DeleteAsync<T>(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            entries.Remove(key);
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<T?> GetAsync<T>(string key)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(key);
            return Task.FromResult(entries.TryGetValue(key, out object? value) ? (T?)value : default);
        }

        /// <inheritdoc />
        public Task ClearAsync()
        {
            entries.Clear();
            Metadata = null;
            ExistsValue = false;
            ClearCount++;
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task<GmailCredentialMetadata?> LoadMetadataAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ThrowOnLoad)
            {
                throw new GmailCredentialStoreException("再認証してください。", new IOException());
            }

            return Task.FromResult(Metadata);
        }

        /// <inheritdoc />
        public Task SaveMetadataAsync(GmailCredentialMetadata metadata, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(metadata);
            cancellationToken.ThrowIfCancellationRequested();
            Metadata = metadata;
            ExistsValue = true;
            return Task.CompletedTask;
        }
    }

    /// <summary>Google OAuth境界を制御するテスト用フローです。</summary>
    private sealed class StubOAuthFlow : IGoogleOAuthFlow
    {
        /// <summary>返却する資格情報を取得または設定します。</summary>
        public UserCredential Credential { get; set; } = CreateCredential(expired: false);

        /// <summary>返却するメールアドレスを取得または設定します。</summary>
        public string EmailAddress { get; set; } = "user@example.com";

        /// <summary>認証開始後にキャンセルまで待機するかを取得または設定します。</summary>
        public bool WaitForCancellation { get; set; }

        /// <summary>認証開始後に明示解放まで待機するかを取得または設定します。</summary>
        public bool WaitForRelease { get; set; }

        /// <summary>認証時に発生させる例外を取得または設定します。</summary>
        public Exception? AuthorizeException { get; set; }

        /// <summary>更新時に発生させる例外を取得または設定します。</summary>
        public Exception? RefreshException { get; set; }

        /// <summary>失効結果を取得または設定します。</summary>
        public bool RevokeResult { get; set; } = true;

        /// <summary>失効時に発生させる例外を取得または設定します。</summary>
        public Exception? RevokeException { get; set; }

        /// <summary>認証開始を通知します。</summary>
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>認証続行を許可します。</summary>
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>認証呼び出し回数を取得します。</summary>
        public int AuthorizeCallCount { get; private set; }

        /// <summary>再認証呼び出し回数を取得します。</summary>
        public int ReauthorizeCallCount { get; private set; }

        /// <summary>トークン更新呼び出し回数を取得します。</summary>
        public int RefreshCallCount { get; private set; }

        /// <inheritdoc />
        public async Task<UserCredential> AuthorizeAsync(ClientSecrets clientSecrets, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(clientSecrets);
            AuthorizeCallCount++;
            Started.TrySetResult();
            if (AuthorizeException is not null)
            {
                throw AuthorizeException;
            }

            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }

            if (WaitForRelease)
            {
                await Release.Task.WaitAsync(cancellationToken);
            }

            return Credential;
        }

        /// <inheritdoc />
        public Task<UserCredential> ReauthorizeAsync(UserCredential credential, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(credential);
            cancellationToken.ThrowIfCancellationRequested();
            ReauthorizeCallCount++;
            return Task.FromResult(credential);
        }

        /// <inheritdoc />
        public Task<UserCredential?> LoadCredentialAsync(ClientSecrets clientSecrets, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(clientSecrets);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<UserCredential?>(Credential);
        }

        /// <inheritdoc />
        public Task<string> GetEmailAddressAsync(UserCredential credential, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(credential);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(EmailAddress);
        }

        /// <inheritdoc />
        public Task<bool> RefreshTokenAsync(UserCredential credential, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(credential);
            cancellationToken.ThrowIfCancellationRequested();
            RefreshCallCount++;
            if (RefreshException is not null)
            {
                throw RefreshException;
            }

            credential.Token.IssuedUtc = DateTime.UtcNow;
            return Task.FromResult(true);
        }

        /// <inheritdoc />
        public Task<bool> RevokeTokenAsync(UserCredential credential, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(credential);
            cancellationToken.ThrowIfCancellationRequested();
            if (RevokeException is not null)
            {
                throw RevokeException;
            }

            return Task.FromResult(RevokeResult);
        }
    }

    /// <summary>固定時刻を返すテスト用時刻プロバイダーです。</summary>
    private sealed class FixedTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset utcNow;

        /// <summary>固定UTC時刻を受け取ります。</summary>
        public FixedTimeProvider(DateTimeOffset utcNow) => this.utcNow = utcNow;

        /// <inheritdoc />
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    /// <summary>整形済みログメッセージだけを記録するテスト用ロガーです。</summary>
    private sealed class CollectingLogger<T> : ILogger<T>
    {
        /// <summary>記録されたメッセージを取得します。</summary>
        public List<string> Messages { get; } = [];

        /// <inheritdoc />
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        /// <inheritdoc />
        public bool IsEnabled(LogLevel logLevel) => true;

        /// <inheritdoc />
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            Messages.Add(formatter(state, exception));
        }
    }
}
