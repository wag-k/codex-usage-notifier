using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// Codex App Serverから利用枠を取得するクライアントを表します。
/// </summary>
public interface ICodexRateLimitClient
{
    /// <summary>
    /// App Serverから利用枠更新通知を受信したときに発生します。
    /// </summary>
    event EventHandler? RateLimitsUpdated;

    /// <summary>
    /// App Serverとの接続が予期せず失われたときに発生します。
    /// </summary>
    event EventHandler? ConnectionLost;

    /// <summary>
    /// 本アプリが起動したApp ServerのプロセスIDを取得します。
    /// </summary>
    int? ProcessId { get; }

    /// <summary>
    /// 現在の利用枠を取得します。
    /// </summary>
    /// <param name="trigger">利用枠を取得する契機です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>内部モデルへ変換された利用枠です。</returns>
    Task<UsageSnapshot> ReadAsync(UsageCheckTrigger trigger, CancellationToken cancellationToken);
}
