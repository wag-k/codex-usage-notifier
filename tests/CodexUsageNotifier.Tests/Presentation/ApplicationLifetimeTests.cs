using CodexUsageNotifier.Presentation;

namespace CodexUsageNotifier.Tests.Presentation;

/// <summary>
/// 明示的な終了要求と非同期終了処理の重複抑止を検証します。
/// </summary>
[TestClass]
public sealed class ApplicationLifetimeTests
{
    /// <summary>終了要求時に登録済み非同期処理を実行することを検証します。</summary>
    [TestMethod]
    public async Task RequestExitAsync_ConfiguredAction_ExecutesAction()
    {
        ApplicationLifetime lifetime = new();
        int callCount = 0;
        lifetime.ConfigureExitAction(() =>
        {
            callCount++;
            return Task.CompletedTask;
        });

        await lifetime.RequestExitAsync();

        Assert.IsTrue(lifetime.IsExitRequested);
        Assert.AreEqual(1, callCount);
    }

    /// <summary>終了メニューを複数回選んでも終了処理を1回だけ実行することを検証します。</summary>
    [TestMethod]
    public async Task RequestExitAsync_MultipleRequests_ExecutesActionOnce()
    {
        ApplicationLifetime lifetime = new();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;
        lifetime.ConfigureExitAction(() =>
        {
            callCount++;
            return completion.Task;
        });

        Task first = lifetime.RequestExitAsync();
        Task second = lifetime.RequestExitAsync();
        completion.SetResult();
        await Task.WhenAll(first, second);

        Assert.AreSame(first, second);
        Assert.AreEqual(1, callCount);
    }

    /// <summary>終了開始後に終了動作を差し替えられないことを検証します。</summary>
    [TestMethod]
    public async Task ConfigureExitAction_AfterExitStarted_RejectsReplacement()
    {
        ApplicationLifetime lifetime = new();
        lifetime.ConfigureExitAction(() => Task.CompletedTask);
        await lifetime.RequestExitAsync();

        Assert.ThrowsException<InvalidOperationException>(
            () => lifetime.ConfigureExitAction(() => Task.CompletedTask));
    }
}
