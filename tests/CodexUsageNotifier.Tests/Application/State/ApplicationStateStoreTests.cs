using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.State;
using CodexUsageNotifier.Domain.Models;

namespace CodexUsageNotifier.Tests.Application.State;

/// <summary>
/// アプリケーション状態管理サービスの直列更新を確認します。
/// </summary>
[TestClass]
public sealed class ApplicationStateStoreTests
{
    /// <summary>
    /// 複数の並行更新が失われず、すべて保存されることを確認します。
    /// </summary>
    [TestMethod]
    public async Task UpdateAsync_ConcurrentUpdates_SerializesAllChanges()
    {
        InMemoryApplicationStateRepository repository = new();
        using ApplicationStateStore store = new(repository);
        Task<ApplicationState>[] updates = Enumerable.Range(0, 20)
            .Select(_ => store.UpdateAsync(
                state => state with { ConsecutiveFailures = state.ConsecutiveFailures + 1 },
                CancellationToken.None))
            .ToArray();

        await Task.WhenAll(updates);
        ApplicationState actual = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(20, actual.ConsecutiveFailures);
        Assert.AreEqual(20, repository.SaveCount);
    }

    /// <summary>
    /// テスト用に状態をメモリ上へ保存するリポジトリです。
    /// </summary>
    private sealed class InMemoryApplicationStateRepository : IApplicationStateRepository
    {
        private ApplicationState state = ApplicationState.CreateDefault();

        /// <summary>
        /// 状態を保存した回数を取得します。
        /// </summary>
        internal int SaveCount { get; private set; }

        /// <summary>
        /// メモリ上の現在状態を返します。
        /// </summary>
        /// <param name="cancellationToken">処理のキャンセル通知です。</param>
        /// <returns>現在状態です。</returns>
        public Task<ApplicationState> LoadAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(state);
        }

        /// <summary>
        /// メモリ上の状態を更新します。
        /// </summary>
        /// <param name="state">保存する状態です。</param>
        /// <param name="cancellationToken">処理のキャンセル通知です。</param>
        public Task SaveAsync(ApplicationState state, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(state);
            this.state = state;
            SaveCount++;
            return Task.CompletedTask;
        }
    }
}
