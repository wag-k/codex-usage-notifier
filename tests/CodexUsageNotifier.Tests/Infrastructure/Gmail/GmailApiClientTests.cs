using System.Net;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Infrastructure.Gmail;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Requests;

namespace CodexUsageNotifier.Tests.Infrastructure.Gmail;

/// <summary>
/// Gmail APIの401、403、および一時障害の分類と認証状態への影響を検証します。
/// </summary>
[TestClass]
public sealed class GmailApiClientTests
{
    /// <summary>401応答で再認証必要状態へ移行することを検証します。</summary>
    [TestMethod]
    public async Task SendRawMessageAsync_Unauthorized_RequiresReauthentication()
    {
        StubAuthenticationService authentication = new();
        ThrowingGateway gateway = new(CreateApiException(HttpStatusCode.Unauthorized));
        GmailApiClient client = new(authentication, gateway);

        GmailApiOperationException exception = await Assert.ThrowsExceptionAsync<GmailApiOperationException>(
            () => client.SendRawMessageAsync("dGVzdA", CancellationToken.None));

        Assert.AreEqual(GmailApiErrorKind.Unauthorized, exception.Kind);
        Assert.IsTrue(authentication.MarkedReauthenticationRequired);
    }

    /// <summary>403応答をAPI有効化または権限確認が必要な失敗として分類することを検証します。</summary>
    [TestMethod]
    public async Task SendRawMessageAsync_Forbidden_ReturnsActionableError()
    {
        GmailApiClient client = new(
            new StubAuthenticationService(),
            new ThrowingGateway(CreateApiException(HttpStatusCode.Forbidden)));

        GmailApiOperationException exception = await Assert.ThrowsExceptionAsync<GmailApiOperationException>(
            () => client.SendRawMessageAsync("dGVzdA", CancellationToken.None));

        Assert.AreEqual(GmailApiErrorKind.Forbidden, exception.Kind);
        StringAssert.Contains(exception.Message, "Gmail API");
    }

    /// <summary>Gmail API未有効化の403をGoogle Cloud設定案内へ変換することを検証します。</summary>
    [TestMethod]
    public async Task SendRawMessageAsync_ApiDisabled_ReturnsEnablementGuidance()
    {
        GoogleApiException apiException = CreateApiException(HttpStatusCode.Forbidden);
        apiException.Error = new RequestError
        {
            Errors = [new SingleError { Reason = "accessNotConfigured" }],
        };
        GmailApiClient client = new(new StubAuthenticationService(), new ThrowingGateway(apiException));

        GmailApiOperationException exception = await Assert.ThrowsExceptionAsync<GmailApiOperationException>(
            () => client.SendRawMessageAsync("dGVzdA", CancellationToken.None));

        StringAssert.Contains(exception.Message, "有効になっていません");
    }

    /// <summary>一時通信障害で認証情報を無効化しないことを検証します。</summary>
    [TestMethod]
    public async Task SendRawMessageAsync_NetworkFailure_DoesNotRequireReauthentication()
    {
        StubAuthenticationService authentication = new();
        GmailApiClient client = new(authentication, new ThrowingGateway(new HttpRequestException("temporary")));

        GmailApiOperationException exception = await Assert.ThrowsExceptionAsync<GmailApiOperationException>(
            () => client.SendRawMessageAsync("dGVzdA", CancellationToken.None));

        Assert.AreEqual(GmailApiErrorKind.Transient, exception.Kind);
        Assert.IsFalse(authentication.MarkedReauthenticationRequired);
    }

    /// <summary>指定HTTP状態を持つGoogle API例外を生成します。</summary>
    private static GoogleApiException CreateApiException(HttpStatusCode statusCode)
    {
        return new GoogleApiException("gmail", "test") { HttpStatusCode = statusCode };
    }

    /// <summary>利用可能な資格情報と再認証記録を提供するテスト用サービスです。</summary>
    private sealed class StubAuthenticationService : IGmailAuthenticationService
    {
        /// <summary>再認証必要状態が記録されたかを取得します。</summary>
        public bool MarkedReauthenticationRequired { get; private set; }

        /// <inheritdoc />
        public Task<GmailAuthenticationStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new GmailAuthenticationStatus
            {
                State = GmailAuthenticationState.Authenticated,
                HasClientConfiguration = true,
                AuthenticatedEmailAddress = "user@example.com",
            });
        }

        /// <inheritdoc />
        public Task<GmailOperationResult> AuthenticateAsync(bool forceReauthentication, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task<GmailOperationResult> DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new NotSupportedException();
        }

        /// <inheritdoc />
        public Task<UserCredential> GetUsableCredentialAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            GoogleAuthorizationCodeFlow flow = new(new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets { ClientId = "test", ClientSecret = "test" },
                Scopes = GoogleOAuthFlow.Scopes,
            });
            return Task.FromResult(new UserCredential(
                flow,
                GoogleOAuthFlow.UserKey,
                new TokenResponse { AccessToken = "test", RefreshToken = "test" }));
        }

        /// <inheritdoc />
        public void MarkReauthenticationRequired(string safeSummary)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(safeSummary);
            MarkedReauthenticationRequired = true;
        }
    }

    /// <summary>指定例外を送信時に発生させるGmailゲートウェイです。</summary>
    private sealed class ThrowingGateway : IGoogleGmailMessageGateway
    {
        private readonly Exception exception;

        /// <summary>発生させる例外を受け取ります。</summary>
        public ThrowingGateway(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            this.exception = exception;
        }

        /// <inheritdoc />
        public Task SendAsync(UserCredential credential, string base64UrlMimeMessage, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(credential);
            ArgumentException.ThrowIfNullOrWhiteSpace(base64UrlMimeMessage);
            cancellationToken.ThrowIfCancellationRequested();
            throw exception;
        }
    }
}
