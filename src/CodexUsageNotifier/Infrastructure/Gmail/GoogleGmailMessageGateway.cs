using CodexUsageNotifier.Application.Gmail;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Services;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// Google公式Gmailクライアントを生成し、認証ユーザー自身としてmessages.sendを実行します。
/// </summary>
public sealed class GoogleGmailMessageGateway : IGoogleGmailMessageGateway
{
    /// <inheritdoc />
    public async Task SendAsync(
        UserCredential credential,
        string base64UrlMimeMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(credential);
        ArgumentException.ThrowIfNullOrWhiteSpace(base64UrlMimeMessage);
        using GmailService service = new(new BaseClientService.Initializer
        {
            ApplicationName = "Codex Usage Notifier",
            HttpClientInitializer = credential,
        });
        Google.Apis.Gmail.v1.Data.Message message = new() { Raw = base64UrlMimeMessage };
        await service.Users.Messages.Send(message, "me").ExecuteAsync(cancellationToken).ConfigureAwait(false);
    }
}
