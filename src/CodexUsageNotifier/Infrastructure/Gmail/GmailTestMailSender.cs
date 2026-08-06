using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Domain.Models;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// 本番通知状態へ接続せず、設定確認用のGmailテストメールだけを送信します。
/// </summary>
public sealed class GmailTestMailSender : IGmailTestMailSender, IDisposable
{
    private static readonly Action<ILogger, string, Exception?> LogTestMailStarted =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4020, "GmailTestMailStarted"),
            "Gmailテストメール送信を開始しました。Recipient={Recipient}");
    private static readonly Action<ILogger, string, Exception?> LogTestMailSucceeded =
        LoggerMessage.Define<string>(LogLevel.Information, new EventId(4021, "GmailTestMailSucceeded"),
            "Gmailテストメール送信に成功しました。Recipient={Recipient}");
    private static readonly Action<ILogger, string, Exception?> LogTestMailFailed =
        LoggerMessage.Define<string>(LogLevel.Warning, new EventId(4022, "GmailTestMailFailed"),
            "Gmailテストメール送信に失敗しました。Reason={Reason}");

    private readonly IGmailAuthenticationStatusProvider statusProvider;
    private readonly IGmailMimeMessageFactory mimeMessageFactory;
    private readonly IGmailApiClient apiClient;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<GmailTestMailSender> logger;
    private readonly SemaphoreSlim sendGate = new(1, 1);

    /// <summary>認証状態、MIME生成、API、時刻、およびログ出力先を受け取ります。</summary>
    public GmailTestMailSender(
        IGmailAuthenticationStatusProvider statusProvider,
        IGmailMimeMessageFactory mimeMessageFactory,
        IGmailApiClient apiClient,
        TimeProvider timeProvider,
        ILogger<GmailTestMailSender> logger)
    {
        ArgumentNullException.ThrowIfNull(statusProvider);
        ArgumentNullException.ThrowIfNull(mimeMessageFactory);
        ArgumentNullException.ThrowIfNull(apiClient);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.statusProvider = statusProvider;
        this.mimeMessageFactory = mimeMessageFactory;
        this.apiClient = apiClient;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <inheritdoc />
    public async Task<GmailOperationResult> SendAsync(string recipient, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        string normalizedRecipient = recipient.Trim();
        if (!AppSettings.IsValidOptionalEmailAddress(normalizedRecipient))
        {
            return new GmailOperationResult { Message = "送信先メールアドレスが不正です。" };
        }

        if (!await sendGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return new GmailOperationResult { Message = "テストメールはすでに送信中です。" };
        }

        try
        {
            GmailAuthenticationStatus status = await statusProvider.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            if (!status.CanSendTestMail || string.IsNullOrWhiteSpace(status.AuthenticatedEmailAddress))
            {
                return new GmailOperationResult { Message = "テストメールを送るにはGoogleアカウントの認証が必要です。" };
            }

            string maskedRecipient = GmailAuthenticationService.MaskEmail(normalizedRecipient);
            LogTestMailStarted(logger, maskedRecipient, null);
            DateTimeOffset localNow = TimeZoneInfo.ConvertTime(timeProvider.GetUtcNow(), TimeZoneInfo.Local);
            string body = $"Codex Usage Notifierからのテストメールです。{Environment.NewLine}{Environment.NewLine}"
                + $"Gmail APIによる認証とメール送信に成功しました。{Environment.NewLine}{Environment.NewLine}"
                + $"認証アカウント：{status.AuthenticatedEmailAddress}{Environment.NewLine}"
                + $"送信先：{normalizedRecipient}{Environment.NewLine}"
                + $"送信時刻：{localNow:yyyy/MM/dd HH:mm}";
            string raw = mimeMessageFactory.CreateBase64UrlMessage(
                status.AuthenticatedEmailAddress,
                normalizedRecipient,
                "Codex Usage Notifier テストメール",
                body);
            await apiClient.SendRawMessageAsync(raw, cancellationToken).ConfigureAwait(false);
            LogTestMailSucceeded(logger, maskedRecipient, null);
            return new GmailOperationResult
            {
                Succeeded = true,
                Message = "テストメールを送信しました。スマートフォンまたはタブレットで通知が届くことを確認してください。",
            };
        }
        catch (GmailApiOperationException exception)
        {
            LogTestMailFailed(logger, exception.Message, null);
            return new GmailOperationResult { Message = exception.Message };
        }
        catch (InvalidOperationException exception)
        {
            LogTestMailFailed(logger, exception.Message, null);
            return new GmailOperationResult { Message = exception.Message };
        }
        finally
        {
            sendGate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        sendGate.Dispose();
    }
}
