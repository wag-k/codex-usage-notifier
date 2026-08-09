using System.Net.Http;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Notifications;

/// <summary>
/// Gmail配送例外を機密情報を含まない再試行分類へ変換します。
/// </summary>
public static class GmailDeliveryFailureClassifier
{
    /// <summary>
    /// 送信例外から永続化可能な失敗分類を返します。
    /// </summary>
    /// <param name="exception">Gmail配送中に発生した例外です。</param>
    /// <returns>自動再試行可否を表す安全な分類です。</returns>
    public static GmailDeliveryFailureKind Classify(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception switch
        {
            GmailApiOperationException { Kind: GmailApiErrorKind.Transient } =>
                GmailDeliveryFailureKind.Transient,
            GmailApiOperationException { Kind: GmailApiErrorKind.Unauthorized } =>
                GmailDeliveryFailureKind.Authentication,
            TimeoutException or TaskCanceledException or HttpRequestException =>
                GmailDeliveryFailureKind.Transient,
            InvalidOperationException => GmailDeliveryFailureKind.Authentication,
            _ => GmailDeliveryFailureKind.Permanent,
        };
    }
}
