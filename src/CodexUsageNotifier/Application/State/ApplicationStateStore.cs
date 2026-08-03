using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Application.State;

/// <summary>
/// プロセス内の最新状態を保持し、状態の更新と永続化を直列化します。
/// </summary>
public sealed class ApplicationStateStore : IDisposable
{
    private readonly IApplicationStateRepository repository;
    private readonly SemaphoreSlim gate = new(1, 1);
    private ApplicationState? currentState;
    private bool disposed;

    /// <summary>
    /// 永続化リポジトリを受け取って状態管理サービスを初期化します。
    /// </summary>
    /// <param name="repository">状態を読み書きするリポジトリです。</param>
    public ApplicationStateStore(IApplicationStateRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        this.repository = repository;
    }

    /// <summary>
    /// メモリ上の最新状態を取得し、未読み込みの場合は永続化済み状態を読み込みます。
    /// </summary>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>最新のアプリケーション状態です。</returns>
    public async Task<ApplicationState> LoadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);

        try
        {
            currentState ??= await repository.LoadAsync(cancellationToken);
            return currentState;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 最新状態へ更新処理を適用し、成功した状態を永続化して保持します。
    /// </summary>
    /// <param name="updater">現在状態から新しい状態を生成する処理です。</param>
    /// <param name="cancellationToken">処理のキャンセル通知です。</param>
    /// <returns>保存された最新状態です。</returns>
    public async Task<ApplicationState> UpdateAsync(
        Func<ApplicationState, ApplicationState> updater,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(updater);
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken);

        try
        {
            currentState ??= await repository.LoadAsync(cancellationToken);
            ApplicationState updatedState = updater(currentState)
                ?? throw new InvalidOperationException("状態更新処理がnullを返しました。");
            await repository.SaveAsync(updatedState, cancellationToken);
            currentState = updatedState;
            return updatedState;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// 状態更新の同期に使用した資源を解放します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        gate.Dispose();
    }
}
