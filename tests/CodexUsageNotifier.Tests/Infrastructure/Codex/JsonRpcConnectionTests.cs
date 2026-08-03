using System.Text.Json;
using CodexUsageNotifier.Infrastructure.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Infrastructure.Codex;

/// <summary>
/// JSONL上のJSON-RPC要求、応答、および異常入力処理を検証します。
/// </summary>
[TestClass]
public sealed class JsonRpcConnectionTests
{
    /// <summary>
    /// 要求IDに対応する応答だけで待機処理が完了することを検証します。
    /// </summary>
    [TestMethod]
    public async Task SendRequestAsync_CompletesMatchingRequestAndWritesJsonLine()
    {
        using StringReader reader = new("{\"id\":1,\"result\":{\"value\":42}}" + Environment.NewLine);
        using StringWriter writer = new();
        await using JsonRpcConnection connection = new(
            reader,
            writer,
            TimeProvider.System,
            NullLogger<JsonRpcConnection>.Instance);

        Task<JsonElement> request = connection.SendRequestAsync(
            "test/read",
            new { sample = true },
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        connection.Start();
        JsonElement result = await request;

        Assert.AreEqual(42, result.GetProperty("value").GetInt32());
        string sentLine = writer.ToString().Trim();
        using JsonDocument sent = JsonDocument.Parse(sentLine);
        Assert.AreEqual(1, sent.RootElement.GetProperty("id").GetInt32());
        Assert.AreEqual("test/read", sent.RootElement.GetProperty("method").GetString());
        Assert.IsFalse(sent.RootElement.TryGetProperty("jsonrpc", out _));
    }

    /// <summary>
    /// 不正なJSONを受信しても次の正しい応答を処理できることを検証します。
    /// </summary>
    [TestMethod]
    public async Task SendRequestAsync_IgnoresMalformedJsonAndContinuesReading()
    {
        string input = "not-json" + Environment.NewLine
            + "{\"id\":1,\"result\":{\"ok\":true}}" + Environment.NewLine;
        using StringReader reader = new(input);
        using StringWriter writer = new();
        await using JsonRpcConnection connection = new(
            reader,
            writer,
            TimeProvider.System,
            NullLogger<JsonRpcConnection>.Instance);

        Task<JsonElement> request = connection.SendRequestAsync(
            "test/read",
            parameters: null,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);
        connection.Start();
        JsonElement result = await request;

        Assert.IsTrue(result.GetProperty("ok").GetBoolean());
    }

    /// <summary>
    /// 利用枠更新通知のメソッド名とパラメーターを通知購読側へ渡すことを検証します。
    /// </summary>
    [TestMethod]
    public async Task Start_RaisesNotificationReceived()
    {
        using StringReader reader = new(
            "{\"method\":\"account/rateLimits/updated\",\"params\":{\"ignored\":true}}"
            + Environment.NewLine);
        using StringWriter writer = new();
        await using JsonRpcConnection connection = new(
            reader,
            writer,
            TimeProvider.System,
            NullLogger<JsonRpcConnection>.Instance);
        TaskCompletionSource<string> notification = new(TaskCreationOptions.RunContinuationsAsynchronously);
        connection.NotificationReceived += (method, _) => notification.TrySetResult(method);

        connection.Start();
        string receivedMethod = await notification.Task.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.AreEqual("account/rateLimits/updated", receivedMethod);
    }

    /// <summary>
    /// 接続障害時に応答待ちの要求を失敗させることを検証します。
    /// </summary>
    [TestMethod]
    public async Task Fail_FailsPendingRequest()
    {
        using StringReader reader = new(string.Empty);
        using StringWriter writer = new();
        await using JsonRpcConnection connection = new(
            reader,
            writer,
            TimeProvider.System,
            NullLogger<JsonRpcConnection>.Instance);
        Task<JsonElement> request = connection.SendRequestAsync(
            "test/read",
            parameters: null,
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        connection.Fail(new IOException("test connection failure"));

        await Assert.ThrowsExceptionAsync<IOException>(() => request);
    }
}
