using CodexUsageNotifier.Application.Abstractions;
using Microsoft.Win32;

namespace CodexUsageNotifier.Infrastructure.WindowsNotifications;

/// <summary>
/// Windowsの電源モード変更イベントからスリープ復帰だけを通知します。
/// </summary>
public sealed class SystemPowerEventSource : IPowerEventSource, IDisposable
{
    private bool disposed;

    /// <summary>
    /// Windows電源イベントの購読を開始します。
    /// </summary>
    public SystemPowerEventSource()
    {
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
    }

    /// <summary>
    /// PCがスリープから復帰したときに発生します。
    /// </summary>
    public event EventHandler? Resumed;

    /// <summary>
    /// Windows電源イベントを受け、復帰イベントだけを転送します。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">電源モード変更内容です。</param>
    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
        {
            Resumed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>
    /// Windows電源イベントの購読を解除します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
    }
}
