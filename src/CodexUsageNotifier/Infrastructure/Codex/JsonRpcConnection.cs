using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace CodexUsageNotifier.Infrastructure.Codex;

/// <summary>
/// 1行1メッセージのJSONLストリーム上でJSON-RPC要求と通知を送受信します。
/// </summary>
internal sealed partial class JsonRpcConnection : IAsyncDisposable
{
    private readonly TextReader reader;
    private readonly TextWriter writer;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<JsonRpcConnection> logger;
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> pendingRequests = new();
    private readonly SemaphoreSlim writeGate = new(1, 1);
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private long nextRequestId;
    private Task? readerTask;
    private int completed;

    /// <summary>
    /// サーバー通知を受信したときに発生します。
    /// </summary>
    public event Action<string, JsonElement?>? NotificationReceived;

    /// <summary>
    /// JSONLの入出力、時刻、およびロガーを受け取って接続を初期化します。
    /// </summary>
    /// <param name="reader">JSON-RPC専用の標準出力です。</param>
    /// <param name="writer">JSON-RPC専用の標準入力です。</param>
    /// <param name="timeProvider">要求タイムアウトに使用する時刻提供元です。</param>
    /// <param name="logger">通信診断を記録するロガーです。</param>
    public JsonRpcConnection(
        TextReader reader,
        TextWriter writer,
        TimeProvider timeProvider,
        ILogger<JsonRpcConnection> logger)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        this.reader = reader;
        this.writer = writer;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>
    /// 標準出力を1行ずつ読み取るバックグラウンド処理を開始します。
    /// </summary>
    public void Start()
    {
        if (readerTask is not null)
        {
            return;
        }

        readerTask = Task.Run(() => ReadLoopAsync(lifetimeCancellation.Token));
    }

    /// <summary>
    /// JSON-RPC要求を送信し、対応するIDの応答を待機します。
    /// </summary>
    /// <param name="method">要求メソッド名です。</param>
    /// <param name="parameters">要求パラメーターです。</param>
    /// <param name="timeout">応答待機のタイムアウトです。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>resultフィールドのJSON値です。</returns>
    public async Task<JsonElement> SendRequestAsync(
        string method,
        object? parameters,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref completed) != 0, this);

        long id = Interlocked.Increment(ref nextRequestId);
        TaskCompletionSource<JsonElement> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!pendingRequests.TryAdd(id, completion))
        {
            throw new InvalidOperationException("JSON-RPC要求IDを登録できませんでした。");
        }

        try
        {
            Dictionary<string, object?> message = new()
            {
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters ?? new { },
            };
            await WriteMessageAsync(message, cancellationToken);
            return await completion.Task.WaitAsync(timeout, timeProvider, cancellationToken);
        }
        finally
        {
            pendingRequests.TryRemove(id, out _);
        }
    }

    /// <summary>
    /// 応答を要求しないJSON-RPC通知を送信します。
    /// </summary>
    /// <param name="method">通知メソッド名です。</param>
    /// <param name="parameters">通知パラメーターです。nullの場合はparams自体を省略します。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    public Task SendNotificationAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref completed) != 0, this);

        Dictionary<string, object?> message = new()
        {
            ["method"] = method,
        };
        if (parameters is not null)
        {
            message["params"] = parameters;
        }

        return WriteMessageAsync(message, cancellationToken);
    }

    /// <summary>
    /// 接続障害を全待機要求へ通知します。
    /// </summary>
    /// <param name="exception">待機要求へ通知する例外です。</param>
    public void Fail(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Complete(exception);
    }

    /// <summary>
    /// JSONメッセージを排他的に1行で書き込みます。
    /// </summary>
    /// <param name="message">送信するメッセージです。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    private async Task WriteMessageAsync(object message, CancellationToken cancellationToken)
    {
        await writeGate.WaitAsync(cancellationToken);
        try
        {
            string json = JsonSerializer.Serialize(message);
            await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
            await writer.FlushAsync(cancellationToken);
        }
        finally
        {
            writeGate.Release();
        }
    }

    /// <summary>
    /// 標準出力を継続的に読み、応答と通知へ振り分けます。
    /// </summary>
    /// <param name="cancellationToken">読み取り停止の通知です。</param>
    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                string? line = await reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    Complete(new EndOfStreamException("Codex App Serverの標準出力が閉じられました。"));
                    return;
                }

                ProcessLine(line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            Complete(exception);
        }
    }

    /// <summary>
    /// 受信した1行を解析し、不正なJSONは記録して読み取りを継続します。
    /// </summary>
    /// <param name="line">受信したJSONLの1行です。</param>
    private void ProcessLine(string line)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (TryCompleteResponse(root))
            {
                return;
            }

            if (root.TryGetProperty("method", out JsonElement methodElement)
                && methodElement.ValueKind == JsonValueKind.String
                && !root.TryGetProperty("id", out _))
            {
                string method = methodElement.GetString()!;
                JsonElement? parameters = root.TryGetProperty("params", out JsonElement paramsElement)
                    ? paramsElement.Clone()
                    : null;
                InvokeNotification(method, parameters);
                return;
            }

            LogUnknownMessage(logger);
        }
        catch (JsonException exception)
        {
            LogInvalidJson(logger, exception);
        }
    }

    /// <summary>
    /// 受信JSONが要求応答であれば、対応する待機処理を完了します。
    /// </summary>
    /// <param name="root">受信JSONのルート要素です。</param>
    /// <returns>要求応答として処理した場合はtrueです。</returns>
    private bool TryCompleteResponse(JsonElement root)
    {
        if (!root.TryGetProperty("id", out JsonElement idElement)
            || idElement.ValueKind != JsonValueKind.Number
            || !idElement.TryGetInt64(out long id)
            || !pendingRequests.TryGetValue(id, out TaskCompletionSource<JsonElement>? completion))
        {
            return false;
        }

        if (root.TryGetProperty("error", out JsonElement errorElement))
        {
            int? code = errorElement.TryGetProperty("code", out JsonElement codeElement)
                && codeElement.TryGetInt32(out int parsedCode)
                    ? parsedCode
                    : null;
            completion.TrySetException(new JsonRpcException(code));
            return true;
        }

        JsonElement result = root.TryGetProperty("result", out JsonElement resultElement)
            ? resultElement.Clone()
            : default;
        completion.TrySetResult(result);
        return true;
    }

    /// <summary>
    /// 通知購読側の例外で読み取りループを停止しないように通知します。
    /// </summary>
    /// <param name="method">通知メソッド名です。</param>
    /// <param name="parameters">通知パラメーターです。</param>
    private void InvokeNotification(string method, JsonElement? parameters)
    {
        try
        {
            NotificationReceived?.Invoke(method, parameters);
        }
        catch (Exception exception)
        {
            LogNotificationHandlerFailed(logger, method, exception);
        }
    }

    /// <summary>
    /// 接続を完了状態にし、待機中の全要求を失敗させます。
    /// </summary>
    /// <param name="exception">待機要求へ通知する例外です。</param>
    private void Complete(Exception exception)
    {
        if (Interlocked.Exchange(ref completed, 1) != 0)
        {
            return;
        }

        foreach (TaskCompletionSource<JsonElement> completion in pendingRequests.Values)
        {
            completion.TrySetException(exception);
        }

        pendingRequests.Clear();
    }

    /// <summary>
    /// 読み取り処理と同期資源を解放します。
    /// </summary>
    /// <returns>解放完了を表す非同期処理です。</returns>
    public async ValueTask DisposeAsync()
    {
        Complete(new ObjectDisposedException(nameof(JsonRpcConnection)));
        lifetimeCancellation.Cancel();
        if (readerTask is not null)
        {
            try
            {
                await readerTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        lifetimeCancellation.Dispose();
        writeGate.Dispose();
    }

    [LoggerMessage(2110, LogLevel.Warning, "App Serverの標準出力から不正なJSONを受信しました。")]
    private static partial void LogInvalidJson(ILogger logger, Exception exception);

    [LoggerMessage(2111, LogLevel.Warning, "App Serverから未対応形式のJSON-RPCメッセージを受信しました。")]
    private static partial void LogUnknownMessage(ILogger logger);

    [LoggerMessage(2112, LogLevel.Error, "JSON-RPC通知ハンドラーが失敗しました。Method={Method}")]
    private static partial void LogNotificationHandlerFailed(ILogger logger, string method, Exception exception);
}
