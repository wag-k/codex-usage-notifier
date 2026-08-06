using System.Text;
using CodexUsageNotifier.Infrastructure.Gmail;
using MimeKit;

namespace CodexUsageNotifier.Tests.Infrastructure.Gmail;

/// <summary>
/// Gmail APIへ渡すMIMEとBase64URLの生成を検証します。
/// </summary>
[TestClass]
public sealed class GmailMimeMessageFactoryTests
{
    /// <summary>From、To、Subject、本文が正しいMIMEとして生成されることを検証します。</summary>
    [TestMethod]
    public void CreateBase64UrlMessage_ValidValues_ContainsExpectedMimeFields()
    {
        GmailMimeMessageFactory factory = new();

        string raw = factory.CreateBase64UrlMessage(
            "sender@example.com",
            "recipient@example.com",
            "Codex Usage Notifier テストメール",
            "日本語の本文です。\r\n2行目です。");
        MimeMessage message = Decode(raw);

        Assert.AreEqual("sender@example.com", message.From.Mailboxes.Single().Address);
        Assert.AreEqual("recipient@example.com", message.To.Mailboxes.Single().Address);
        Assert.AreEqual("Codex Usage Notifier テストメール", message.Subject);
        Assert.IsNotNull(message.TextBody);
        StringAssert.Contains(message.TextBody, "日本語の本文です。");
    }

    /// <summary>日本語の件名と本文がUTF-8で往復できることを検証します。</summary>
    [TestMethod]
    public void CreateBase64UrlMessage_JapaneseContent_RoundTripsUtf8()
    {
        GmailMimeMessageFactory factory = new();
        const string subject = "週間枠のリセットが近づいています";
        const string body = "残量：65%\r\nリセットまで：23時間";

        MimeMessage message = Decode(factory.CreateBase64UrlMessage(
            "sender@example.com", "recipient@example.com", subject, body));

        Assert.AreEqual(subject, message.Subject);
        Assert.AreEqual(body, message.TextBody);
    }

    /// <summary>Base64URLに標準Base64固有文字と末尾パディングが残らないことを検証します。</summary>
    [TestMethod]
    public void CreateBase64UrlMessage_AnyMessage_UsesUnpaddedBase64Url()
    {
        GmailMimeMessageFactory factory = new();

        string raw = factory.CreateBase64UrlMessage(
            "sender@example.com", "recipient@example.com", "件名", "本文+/=");

        Assert.IsFalse(raw.Contains('+'));
        Assert.IsFalse(raw.Contains('/'));
        Assert.IsFalse(raw.EndsWith('='));
    }

    /// <summary>MIME全体の改行がCRLFであり、単独LFを含まないことを検証します。</summary>
    [TestMethod]
    public void CreateBase64UrlMessage_MimeMessage_UsesCrLfNewLines()
    {
        GmailMimeMessageFactory factory = new();
        string raw = factory.CreateBase64UrlMessage(
            "sender@example.com", "recipient@example.com", "件名", "1行目\n2行目");

        byte[] bytes = DecodeBytes(raw);

        for (int index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] == (byte)'\n')
            {
                Assert.IsTrue(index > 0 && bytes[index - 1] == (byte)'\r');
            }
        }
    }

    /// <summary>要求するOAuthスコープが送信と最小OIDC権限だけであることを検証します。</summary>
    [TestMethod]
    public void GoogleOAuthScopes_Default_ContainsOnlyMinimumScopes()
    {
        CollectionAssert.AreEquivalent(
            new[] { "https://www.googleapis.com/auth/gmail.send", "openid", "email" },
            GoogleOAuthFlow.Scopes);
    }

    /// <summary>Base64URLを復元してMIMEとして読み込みます。</summary>
    private static MimeMessage Decode(string raw)
    {
        byte[] bytes = DecodeBytes(raw);
        using MemoryStream stream = new(bytes);
        return MimeMessage.Load(stream);
    }

    /// <summary>Base64URL文字列を元のMIMEバイト列へ戻します。</summary>
    private static byte[] DecodeBytes(string raw)
    {
        string base64 = raw.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + ((4 - (base64.Length % 4)) % 4), '=');
        return Convert.FromBase64String(base64);
    }
}
