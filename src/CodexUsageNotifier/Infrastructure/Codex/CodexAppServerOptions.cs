namespace CodexUsageNotifier.Infrastructure.Codex;

/// <summary>
/// Codex App Serverの起動と通信に使用する設定を表します。
/// </summary>
public sealed class CodexAppServerOptions
{
    /// <summary>
    /// Codex CLIの実行ファイル名またはパスを取得または設定します。
    /// </summary>
    public string ExecutablePath { get; set; } = "codex";

    /// <summary>
    /// App Server起動のタイムアウトを取得または設定します。
    /// </summary>
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// initialize要求のタイムアウトを取得または設定します。
    /// </summary>
    public TimeSpan InitializeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// 通常要求のタイムアウトを取得または設定します。
    /// </summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// 正常終了を待つ時間を取得または設定します。
    /// </summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
