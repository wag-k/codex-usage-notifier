using System.Net;
using System.Net.Http;
using CodexUsageNotifier.Application.Gmail;
using Google;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// 認証済みユーザー自身としてGmail users.messages.sendを呼び出します。
/// </summary>
public sealed class GmailApiClient : IGmailApiClient
{
    private readonly IGmailAuthenticationService authenticationService;
    private readonly IGoogleGmailMessageGateway gateway;

    /// <summary>利用可能なGoogle資格情報の取得元を受け取ります。</summary>
    public GmailApiClient(
        IGmailAuthenticationService authenticationService,
        IGoogleGmailMessageGateway gateway)
    {
        ArgumentNullException.ThrowIfNull(authenticationService);
        ArgumentNullException.ThrowIfNull(gateway);
        this.authenticationService = authenticationService;
        this.gateway = gateway;
    }

    /// <inheritdoc />
    public async Task SendRawMessageAsync(string base64UrlMimeMessage, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(base64UrlMimeMessage);
        try
        {
            Google.Apis.Auth.OAuth2.UserCredential credential =
                await authenticationService.GetUsableCredentialAsync(cancellationToken).ConfigureAwait(false);
            await gateway.SendAsync(credential, base64UrlMimeMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.Unauthorized)
        {
            authenticationService.MarkReauthenticationRequired("Gmail APIの認証が無効です。Googleアカウントを再認証してください。");
            throw new GmailApiOperationException(
                GmailApiErrorKind.Unauthorized,
                "Gmail APIの認証が無効です。再認証してください。",
                exception);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.Forbidden)
        {
            if (IsGmailAuthorizationFailure(exception))
            {
                authenticationService.MarkReauthenticationRequired(
                    "Gmail送信権限を確認できません。Googleアカウントを再認証してください。");
                throw new GmailApiOperationException(
                    GmailApiErrorKind.AuthorizationRequired,
                    "Gmail送信権限が不足しています。Googleアカウントを再認証してください。",
                    exception);
            }

            if (IsGmailApiDisabled(exception))
            {
                throw new GmailApiOperationException(
                    GmailApiErrorKind.ApiNotEnabled,
                    "Google CloudプロジェクトでGmail APIが有効になっていません。有効化後に再試行してください。",
                    exception);
            }

            throw new GmailApiOperationException(
                GmailApiErrorKind.Forbidden,
                "Gmail APIから送信を拒否されました。Google Cloudまたはアカウントの設定を確認してください。",
                exception);
        }
        catch (GoogleApiException exception) when (exception.HttpStatusCode == HttpStatusCode.TooManyRequests)
        {
            throw new GmailApiOperationException(
                GmailApiErrorKind.Transient,
                "Gmail APIの送信回数制限に達しました。時間を置いて再試行してください。",
                exception);
        }
        catch (GoogleApiException exception) when ((int)exception.HttpStatusCode >= 500)
        {
            throw new GmailApiOperationException(
                GmailApiErrorKind.Transient,
                "Gmail APIで一時的なサーバーエラーが発生しました。時間を置いて再試行してください。",
                exception);
        }
        catch (HttpRequestException exception)
        {
            throw new GmailApiOperationException(
                GmailApiErrorKind.Transient,
                "ネットワークへ接続できません。接続を確認して再試行してください。",
                exception);
        }
        catch (GoogleApiException exception)
        {
            throw new GmailApiOperationException(
                GmailApiErrorKind.Unknown,
                "Gmail APIでメールを送信できませんでした。",
                exception);
        }
    }

    /// <summary>403の理由がGoogle Cloud側のAPI未有効化を示すか判定します。</summary>
    private static bool IsGmailApiDisabled(GoogleApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Error?.Errors?.Any(error =>
            string.Equals(error.Reason, "accessNotConfigured", StringComparison.OrdinalIgnoreCase)
            || string.Equals(error.Reason, "serviceDisabled", StringComparison.OrdinalIgnoreCase)) == true;
    }

    /// <summary>403の理由が再認証で解消できる認可不足を示すか判定します。</summary>
    private static bool IsGmailAuthorizationFailure(GoogleApiException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception.Error?.Errors?.Any(error =>
            string.Equals(error.Reason, "insufficientPermissions", StringComparison.OrdinalIgnoreCase)
            || string.Equals(error.Reason, "insufficientAuthenticationScopes", StringComparison.OrdinalIgnoreCase)
            || string.Equals(error.Reason, "authError", StringComparison.OrdinalIgnoreCase)) == true;
    }
}
