namespace CodexUsageNotifier.Presentation;

/// <summary>
/// WPFアプリケーションの明示的な終了要求を管理します。
/// </summary>
public sealed class ApplicationLifetime
{
    /// <summary>
    /// ユーザーが明示的に終了を要求したかどうかを取得します。
    /// </summary>
    public bool IsExitRequested { get; private set; }

    /// <summary>
    /// 終了要求を記録し、WPFアプリケーションを終了します。
    /// </summary>
    public void RequestExit()
    {
        IsExitRequested = true;
        System.Windows.Application.Current.Shutdown();
    }
}
