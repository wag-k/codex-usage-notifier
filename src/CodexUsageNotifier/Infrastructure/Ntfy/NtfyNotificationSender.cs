using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using CodexUsageNotifier.Application.Ntfy;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Infrastructure.Ntfy;

/// <summary>共有HttpClientで匿名ntfy.sh JSON APIへ通知を送信します。</summary>
public sealed class NtfyNotificationSender : INtfyNotificationSender
{
    private static readonly Uri Endpoint = new("https://ntfy.sh/");
    private readonly HttpClient httpClient;

    /// <summary>DIで管理される再利用可能なHttpClientを受け取ります。</summary>
    public NtfyNotificationSender(HttpClient httpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        this.httpClient = httpClient;
    }

    /// <inheritdoc />
    public async Task SendAsync(NtfyTopic topic, NtfyNotificationMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(topic);
        ArgumentNullException.ThrowIfNull(message);
        if (!NtfyTopicValidator.IsValid(topic.Value))
        {
            throw new NtfyDeliveryException("スマホ通知Topicの形式が不正です。", NtfyDeliveryFailureKind.Permanent);
        }
        try
        {
            using HttpResponseMessage response = await httpClient.PostAsJsonAsync(
                Endpoint,
                new { topic = topic.Value, title = message.Title, message = message.Body, priority = 3 },
                cancellationToken).ConfigureAwait(false);
            if (response.IsSuccessStatusCode)
            {
                return;
            }

            NtfyDeliveryFailureKind kind = response.StatusCode is HttpStatusCode.RequestTimeout
                or HttpStatusCode.TooManyRequests || (int)response.StatusCode >= 500
                    ? NtfyDeliveryFailureKind.Transient
                    : NtfyDeliveryFailureKind.Permanent;
            throw new NtfyDeliveryException(
                $"スマホ通知サービスが要求を受理しませんでした（HTTP {(int)response.StatusCode}）。",
                kind);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new NtfyDeliveryException("スマホ通知サービスへの接続がタイムアウトしました。", NtfyDeliveryFailureKind.Transient, exception);
        }
        catch (HttpRequestException exception)
        {
            throw new NtfyDeliveryException("スマホ通知サービスへ接続できませんでした。", NtfyDeliveryFailureKind.Transient, exception);
        }
    }
}
