namespace CodexUsageNotifier.Presentation;

/// <summary>
/// WPFアプリケーションの明示的な終了要求を管理します。
/// </summary>
public sealed class ApplicationLifetime
{
    private readonly object syncRoot = new();
    private Func<Task>? exitAction;
    private Task? exitTask;

    /// <summary>
    /// ユーザーが明示的に終了を要求したかどうかを取得します。
    /// </summary>
    public bool IsExitRequested { get; private set; }

    /// <summary>
    /// アプリケーション固有の非同期終了処理を登録します。
    /// </summary>
    /// <param name="action">監視や子プロセスを解放してからWPFを終了する処理です。</param>
    public void ConfigureExitAction(Func<Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (syncRoot)
        {
            if (exitTask is not null)
            {
                throw new InvalidOperationException("終了処理の開始後は終了動作を変更できません。");
            }

            exitAction = action;
        }
    }

    /// <summary>
    /// 終了要求を記録し、登録済みの非同期終了処理を最大1回実行します。
    /// </summary>
    /// <returns>アプリケーションの終了準備を表す処理です。</returns>
    public Task RequestExitAsync()
    {
        lock (syncRoot)
        {
            if (exitTask is not null)
            {
                return exitTask;
            }

            IsExitRequested = true;
            exitTask = exitAction is null
                ? ShutdownCurrentApplicationAsync()
                : exitAction();
            return exitTask;
        }
    }

    /// <summary>専用終了処理が未登録の場合に現在のWPFアプリケーションを終了します。</summary>
    /// <returns>完了済みの処理です。</returns>
    private static Task ShutdownCurrentApplicationAsync()
    {
        System.Windows.Application.Current?.Shutdown();
        return Task.CompletedTask;
    }
}
