using CodexUsageNotifier.Application.Gmail;
using MimeKit;

namespace CodexUsageNotifier.Infrastructure.Gmail;

/// <summary>
/// UTF-8のテストメールをMIME化し、Gmail API用Base64URLへ変換します。
/// </summary>
public sealed class GmailMimeMessageFactory : IGmailMimeMessageFactory
{
    /// <inheritdoc />
    public string CreateBase64UrlMessage(
        string senderAddress,
        string recipientAddress,
        string subject,
        string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(senderAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientAddress);
        ArgumentException.ThrowIfNullOrWhiteSpace(subject);
        ArgumentNullException.ThrowIfNull(body);

        MimeMessage message = new();
        message.From.Add(MailboxAddress.Parse(senderAddress));
        message.To.Add(MailboxAddress.Parse(recipientAddress));
        message.Subject = subject;
        message.Body = new TextPart("plain")
        {
            ContentTransferEncoding = ContentEncoding.Base64,
            Text = body,
        };

        FormatOptions options = FormatOptions.Default.Clone();
        options.NewLineFormat = NewLineFormat.Dos;
        using MemoryStream stream = new();
        message.WriteTo(options, stream);
        return Convert.ToBase64String(stream.ToArray())
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
