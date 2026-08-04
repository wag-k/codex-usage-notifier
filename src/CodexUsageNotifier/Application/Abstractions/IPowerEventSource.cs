namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// Windowsのスリープ復帰イベントをアプリケーション層へ通知します。
/// </summary>
public interface IPowerEventSource
{
    /// <summary>
    /// PCがスリープから復帰したときに発生します。
    /// </summary>
    event EventHandler? Resumed;
}
