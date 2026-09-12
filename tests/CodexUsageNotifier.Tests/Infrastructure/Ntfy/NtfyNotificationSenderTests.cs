using System.Net;
using System.Text.Json;
using CodexUsageNotifier.Application.Ntfy;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Ntfy;

namespace CodexUsageNotifier.Tests.Infrastructure.Ntfy;

/// <summary>ntfy HTTP APIの安全な要求形式と失敗分類を検証します。</summary>
[TestClass]
public sealed class NtfyNotificationSenderTests
{
    /// <summary>ntfy送信のためだけに配布環境でHttp.Jsonを追加ロードしないことを検証します。</summary>
    [TestMethod]
    public void SenderAssembly_DoesNotRequireHttpJson()
    {
        Assert.IsFalse(typeof(NtfyNotificationSender).Assembly.GetReferencedAssemblies()
            .Any(assembly => assembly.Name == "System.Net.Http.Json"));
    }

    /// <summary>TopicをURLへ含めず、ルートへpriority 3のJSONをPOSTすることを検証します。</summary>
    [TestMethod]
    public async Task SendAsync_ValidMessage_PostsJsonToRoot()
    {
        RecordingHandler handler = new(HttpStatusCode.OK);
        NtfyNotificationSender sender = new(new HttpClient(handler));

        await sender.SendAsync(CreateTopic(), new NtfyNotificationMessage { Title = "週間枠", Body = "本文" }, CancellationToken.None);

        Assert.AreEqual(new Uri("https://ntfy.sh/"), handler.RequestUri);
        Assert.AreEqual(HttpMethod.Post, handler.Method);
        Assert.AreEqual("application/json", handler.ContentType);
        Assert.AreEqual("utf-8", handler.CharSet);
        using JsonDocument json = JsonDocument.Parse(handler.Body!);
        Assert.AreEqual("secret-topic", json.RootElement.GetProperty("topic").GetString());
        Assert.AreEqual("週間枠", json.RootElement.GetProperty("title").GetString());
        Assert.AreEqual("本文", json.RootElement.GetProperty("message").GetString());
        Assert.AreEqual(3, json.RootElement.GetProperty("priority").GetInt32());
    }

    /// <summary>HTTP 408、429、5xxだけを一時障害として分類することを検証します。</summary>
    [DataTestMethod]
    [DataRow(HttpStatusCode.RequestTimeout, NtfyDeliveryFailureKind.Transient)]
    [DataRow(HttpStatusCode.TooManyRequests, NtfyDeliveryFailureKind.Transient)]
    [DataRow(HttpStatusCode.InternalServerError, NtfyDeliveryFailureKind.Transient)]
    [DataRow(HttpStatusCode.BadRequest, NtfyDeliveryFailureKind.Permanent)]
    [DataRow(HttpStatusCode.Unauthorized, NtfyDeliveryFailureKind.Permanent)]
    [DataRow(HttpStatusCode.NotFound, NtfyDeliveryFailureKind.Permanent)]
    public async Task SendAsync_HttpFailure_ClassifiesRetry(HttpStatusCode status, NtfyDeliveryFailureKind expected)
    {
        NtfyNotificationSender sender = new(new HttpClient(new RecordingHandler(status)));
        NtfyDeliveryException exception = await Assert.ThrowsExceptionAsync<NtfyDeliveryException>(() =>
            sender.SendAsync(CreateTopic(), new NtfyNotificationMessage { Title = "title", Body = "body" }, CancellationToken.None));
        Assert.AreEqual(expected, exception.FailureKind);
    }

    /// <summary>ネットワーク例外を一時障害として分類することを検証します。</summary>
    [TestMethod]
    public async Task SendAsync_HttpRequestException_IsTransient()
    {
        NtfyNotificationSender sender = new(new HttpClient(new ThrowingHandler(new HttpRequestException("offline"))));
        NtfyDeliveryException exception = await Assert.ThrowsExceptionAsync<NtfyDeliveryException>(() =>
            sender.SendAsync(CreateTopic(), new NtfyNotificationMessage { Title = "title", Body = "body" }, CancellationToken.None));
        Assert.AreEqual(NtfyDeliveryFailureKind.Transient, exception.FailureKind);
    }

    /// <summary>HttpClient内部タイムアウト相当のキャンセルを一時障害として分類することを検証します。</summary>
    [TestMethod]
    public async Task SendAsync_InternalTimeout_IsTransient()
    {
        NtfyNotificationSender sender = new(new HttpClient(new ThrowingHandler(new TaskCanceledException("timeout"))));
        NtfyDeliveryException exception = await Assert.ThrowsExceptionAsync<NtfyDeliveryException>(() =>
            sender.SendAsync(CreateTopic(), new NtfyNotificationMessage { Title = "title", Body = "body" }, CancellationToken.None));
        Assert.AreEqual(NtfyDeliveryFailureKind.Transient, exception.FailureKind);
    }

    /// <summary>テスト用Topicを生成します。</summary>
    private static NtfyTopic CreateTopic() => new() { Value = "secret-topic", GenerationId = "gen", CreatedAtUtc = DateTimeOffset.UtcNow };

    /// <summary>外部通信を行わずHTTP要求を記録します。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode status;

        /// <summary>返すHTTP状態を指定します。</summary>
        public RecordingHandler(HttpStatusCode status) => this.status = status;

        /// <summary>要求URIを取得します。</summary>
        public Uri? RequestUri { get; private set; }

        /// <summary>送信されたHTTPメソッドを取得します。</summary>
        public HttpMethod? Method { get; private set; }

        /// <summary>Content-Typeを取得します。</summary>
        public string? ContentType { get; private set; }

        /// <summary>日本語本文の送信文字コードを取得します。</summary>
        public string? CharSet { get; private set; }

        /// <summary>JSON本文を取得します。</summary>
        public string? Body { get; private set; }

        /// <inheritdoc />
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            ContentType = request.Content?.Headers.ContentType?.MediaType;
            CharSet = request.Content?.Headers.ContentType?.CharSet;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status);
        }
    }

    /// <summary>指定例外を外部通信なしで発生させます。</summary>
    private sealed class ThrowingHandler : HttpMessageHandler
    {
        private readonly Exception exception;

        /// <summary>送信時に発生させる例外を受け取ります。</summary>
        public ThrowingHandler(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            this.exception = exception;
        }

        /// <inheritdoc />
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromException<HttpResponseMessage>(exception);
        }
    }
}
