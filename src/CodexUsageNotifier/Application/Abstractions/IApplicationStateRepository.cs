using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// アプリケーションの実行状態の永続化を抽象化します。
/// </summary>
public interface IApplicationStateRepository
{
    /// <summary>
    /// 保存済み状態を読み込み、存在しない場合は初期状態を返します。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>読み込んだ状態です。</returns>
    Task<ApplicationState> LoadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// 状態を一時ファイル経由で安全に保存します。
    /// </summary>
    /// <param name="state">保存する状態です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    Task SaveAsync(ApplicationState state, CancellationToken cancellationToken);
}
