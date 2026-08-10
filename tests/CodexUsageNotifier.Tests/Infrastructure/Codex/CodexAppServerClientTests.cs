using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Versioning;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Codex;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Infrastructure.Codex;

/// <summary>
/// App Serverクライアントの初期化順序と利用枠要求を検証します。
/// </summary>
[TestClass]
public sealed class CodexAppServerClientTests
{
    /// <summary>
    /// initialize、initialized、利用枠取得の順で送信し、所有プロセスIDと結果を返すことを検証します。
    /// </summary>
    [TestMethod]
    public async Task ReadAsync_InitializesBeforeReadingRateLimits()
    {
        FakeCodexProcess process = new();
        FakeProcessFactory factory = new(process);
        ApplicationVersionProvider versionProvider = new("9.8.7+test");
        await using CodexAppServerClient client = new(
            factory,
            new CodexAppServerOptions(),
            TimeProvider.System,
            NullLoggerFactory.Instance,
            NullLogger<CodexAppServerClient>.Instance,
            versionProvider);

        UsageSnapshot result = await client.ReadAsync(
            UsageCheckTrigger.Startup,
            CancellationToken.None);

        Assert.AreEqual(4321, client.ProcessId);
        Assert.AreEqual(90D, result.RateLimits.Single().RemainingPercent);
        string[] methods = process.SentLines
            .Select(line => JsonDocument.Parse(line))
            .Select(document => document.RootElement.GetProperty("method").GetString()!)
            .ToArray();
        CollectionAssert.AreEqual(
            new[] { "initialize", "initialized", "account/rateLimits/read" },
            methods);
        using JsonDocument initialize = JsonDocument.Parse(process.SentLines.First());
        Assert.AreEqual(
            versionProvider.Version,
            initialize.RootElement.GetProperty("params").GetProperty("clientInfo").GetProperty("version").GetString());
    }

    /// <summary>
    /// 指定した偽プロセスだけを返すファクトリです。
    /// </summary>
    private sealed class FakeProcessFactory : ICodexAppServerProcessFactory
    {
        private readonly ICodexAppServerProcess process;

        /// <summary>
        /// 返却対象の偽プロセスを受け取ります。
        /// </summary>
        /// <param name="process">返却する偽プロセスです。</param>
        public FakeProcessFactory(ICodexAppServerProcess process)
        {
            ArgumentNullException.ThrowIfNull(process);
            this.process = process;
        }

        /// <summary>
        /// 偽プロセスを返します。
        /// </summary>
        /// <param name="cancellationToken">起動のキャンセル通知です。</param>
        /// <returns>偽プロセスです。</returns>
        public Task<ICodexAppServerProcess> StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(process);
        }
    }

    /// <summary>
    /// 書き込まれた要求へ自動応答する所有プロセスのテスト実装です。
    /// </summary>
    private sealed class FakeCodexProcess : ICodexAppServerProcess
    {
        private readonly ChannelLineReader output = new();
        private readonly TaskCompletionSource exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly RespondingWriter input;

        /// <summary>
        /// 要求へ応答する標準入出力を初期化します。
        /// </summary>
        public FakeCodexProcess()
        {
            input = new RespondingWriter(output);
        }

        /// <summary>
        /// 偽のプロセスIDを取得します。
        /// </summary>
        public int Id => 4321;

        /// <summary>
        /// 偽プロセスが終了済みかどうかを取得します。
        /// </summary>
        public bool HasExited { get; private set; }

        /// <summary>
        /// 応答生成付きの標準入力を取得します。
        /// </summary>
        public TextWriter StandardInput => input;

        /// <summary>
        /// チャネルで供給する標準出力を取得します。
        /// </summary>
        public TextReader StandardOutput => output;

        /// <summary>
        /// 空の標準エラー出力を取得します。
        /// </summary>
        public TextReader StandardError { get; } = new StringReader(string.Empty);

        /// <summary>
        /// クライアントが送信したJSONLを取得します。
        /// </summary>
        public IReadOnlyCollection<string> SentLines => input.SentLines;

        /// <summary>
        /// 標準入力を閉じて終了状態にします。
        /// </summary>
        public void CloseStandardInput()
        {
            HasExited = true;
            exit.TrySetResult();
            output.Complete();
        }

        /// <summary>
        /// 偽プロセスの終了を待機します。
        /// </summary>
        /// <param name="cancellationToken">待機のキャンセル通知です。</param>
        /// <returns>終了待機処理です。</returns>
        public Task WaitForExitAsync(CancellationToken cancellationToken) => exit.Task.WaitAsync(cancellationToken);

        /// <summary>
        /// 偽プロセスを強制終了状態にします。
        /// </summary>
        public void KillProcessTree() => CloseStandardInput();

        /// <summary>
        /// 偽プロセスを解放します。
        /// </summary>
        /// <returns>解放完了を表します。</returns>
        public ValueTask DisposeAsync()
        {
            StandardError.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// チャネルから1行ずつ読み取るテスト用TextReaderです。
    /// </summary>
    private sealed class ChannelLineReader : TextReader
    {
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();

        /// <summary>
        /// 次のJSONL行をチャネルへ追加します。
        /// </summary>
        /// <param name="line">追加する行です。</param>
        public void Add(string line) => lines.Writer.TryWrite(line);

        /// <summary>
        /// 行の供給を完了します。
        /// </summary>
        public void Complete() => lines.Writer.TryComplete();

        /// <summary>
        /// 次の行を非同期に読み取ります。
        /// </summary>
        /// <param name="cancellationToken">読み取りのキャンセル通知です。</param>
        /// <returns>次の行、または完了時はnullです。</returns>
        public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
        {
            return await lines.Reader.WaitToReadAsync(cancellationToken)
                && lines.Reader.TryRead(out string? line)
                    ? line
                    : null;
        }
    }

    /// <summary>
    /// JSON-RPC要求を記録し、メソッドに対応する応答を生成するテスト用TextWriterです。
    /// </summary>
    private sealed class RespondingWriter : TextWriter
    {
        private readonly ChannelLineReader output;
        private readonly ConcurrentQueue<string> sentLines = new();

        /// <summary>
        /// 応答の出力先を受け取ります。
        /// </summary>
        /// <param name="output">サーバー応答を供給する出力先です。</param>
        public RespondingWriter(ChannelLineReader output)
        {
            ArgumentNullException.ThrowIfNull(output);
            this.output = output;
        }

        /// <summary>
        /// UTF-8を使用することを表します。
        /// </summary>
        public override Encoding Encoding => Encoding.UTF8;

        /// <summary>
        /// 送信済みのJSONL行を取得します。
        /// </summary>
        public IReadOnlyCollection<string> SentLines => sentLines.ToArray();

        /// <summary>
        /// 送信行を記録し、要求IDがあるメソッドへ応答します。
        /// </summary>
        /// <param name="buffer">送信されたJSON文字列です。</param>
        /// <param name="cancellationToken">書き込みのキャンセル通知です。</param>
        /// <returns>書き込み完了を表します。</returns>
        public override Task WriteLineAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string line = buffer.ToString();
            sentLines.Enqueue(line);
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("id", out JsonElement id))
            {
                return Task.CompletedTask;
            }

            string method = root.GetProperty("method").GetString()!;
            string result = method == "initialize"
                ? "{}"
                : "{\"rateLimits\":{\"limitId\":\"codex\",\"primary\":{\"usedPercent\":10,\"windowDurationMins\":300}}}";
            output.Add($"{{\"id\":{id.GetInt64()},\"result\":{result}}}");
            return Task.CompletedTask;
        }
    }
}
