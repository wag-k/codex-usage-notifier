using CodexUsageNotifier.Application.Gmail;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// Phase 4BのMIME生成とGmail APIクライアントを使って本番通知メールを送信します。
/// </summary>
public sealed class GmailNotificationSender : IGmailNotificationSender
{
    private readonly IGmailAuthenticationStatusProvider statusProvider;
    private readonly IGmailMimeMessageFactory mimeMessageFactory;
    private readonly IGmailApiClient apiClient;

    /// <summary>認証状態、共通MIME生成、およびGmail APIクライアントを受け取ります。</summary>
    public GmailNotificationSender(
        IGmailAuthenticationStatusProvider statusProvider,
        IGmailMimeMessageFactory mimeMessageFactory,
        IGmailApiClient apiClient)
    {
        ArgumentNullException.ThrowIfNull(statusProvider);
        ArgumentNullException.ThrowIfNull(mimeMessageFactory);
        ArgumentNullException.ThrowIfNull(apiClient);
        this.statusProvider = statusProvider;
        this.mimeMessageFactory = mimeMessageFactory;
        this.apiClient = apiClient;
    }

    /// <inheritdoc />
    public async Task SendAsync(
        string recipient,
        GmailNotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        ArgumentNullException.ThrowIfNull(message);
        GmailAuthenticationStatus status = await statusProvider.GetStatusAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!status.CanSendMail || string.IsNullOrWhiteSpace(status.AuthenticatedEmailAddress))
        {
            throw new InvalidOperationException("Gmail本番通知を送るにはGoogleアカウントの認証が必要です。");
        }

        string rawMessage = mimeMessageFactory.CreateBase64UrlMessage(
            status.AuthenticatedEmailAddress,
            recipient,
            message.Subject,
            message.Body);
        await apiClient.SendRawMessageAsync(rawMessage, cancellationToken).ConfigureAwait(false);
    }
}
