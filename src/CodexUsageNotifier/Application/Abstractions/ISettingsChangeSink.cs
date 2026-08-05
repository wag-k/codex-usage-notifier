using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// 保存済み設定を再起動なしで実行中サービスへ反映する受け口です。
/// </summary>
public interface ISettingsChangeSink
{
    /// <summary>
    /// 新しい設定で待機スケジュールと表示を更新し、利用枠の即時取得は行いません。
    /// </summary>
    /// <param name="settings">保存に成功した新しい設定です。</param>
    /// <param name="cancellationToken">反映処理のキャンセル通知です。</param>
    /// <returns>反映完了を表す非同期処理です。</returns>
    Task ApplyAsync(AppSettings settings, CancellationToken cancellationToken);
}
