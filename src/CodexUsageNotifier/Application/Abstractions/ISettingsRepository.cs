using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// アプリケーション設定の永続化を抽象化します。
/// </summary>
public interface ISettingsRepository
{
    /// <summary>
    /// 保存済み設定を読み込み、存在しない場合は初期設定を返します。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>読み込んだ設定です。</returns>
    Task<AppSettings> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 検証済みの設定を保存します。
    /// </summary>
    /// <param name="settings">保存する設定です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken);
}
