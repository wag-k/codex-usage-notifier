using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// 利用枠監視の状態を画面などへ通知する出力先を表します。
/// </summary>
public interface IUsageStatusSink
{
    /// <summary>
    /// 利用枠の取得開始を通知します。
    /// </summary>
    void SetChecking();

    /// <summary>
    /// 正常に取得した利用枠を通知します。
    /// </summary>
    /// <param name="snapshot">取得した利用枠です。</param>
    void SetSnapshot(UsageSnapshot snapshot);

    /// <summary>
    /// 利用枠取得の失敗を通知します。
    /// </summary>
    /// <param name="consecutiveFailures">現在の連続失敗回数です。</param>
    /// <param name="message">機密情報を含まないエラー概要です。</param>
    void SetFailure(int consecutiveFailures, string message);
}
