namespace CodexUsageNotifier.Infrastructure.Codex;

/// <summary>
/// JSON-RPCサーバーが返したエラーを表します。
/// </summary>
public sealed class JsonRpcException : Exception
{
    /// <summary>
    /// JSON-RPCエラーコードを取得します。
    /// </summary>
    public int? Code { get; }

    /// <summary>
    /// エラーコードを受け取って例外を初期化します。
    /// </summary>
    /// <param name="code">JSON-RPCエラーコードです。</param>
    public JsonRpcException(int? code)
        : base(code is null ? "JSON-RPC要求が失敗しました。" : $"JSON-RPC要求が失敗しました。Code={code}")
    {
        Code = code;
    }
}
