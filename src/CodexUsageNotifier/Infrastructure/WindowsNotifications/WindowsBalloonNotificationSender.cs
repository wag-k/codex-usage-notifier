using CodexUsageNotifier.Application.Abstractions;
using CodexUsageNotifier.Application.Notifications;
using CodexUsageNotifier.Presentation.Tray;

namespace CodexUsageNotifier.Infrastructure.WindowsNotifications;

/// <summary>
/// 共有タスクトレイアイコンを使用してWindowsバルーン通知を表示します。
/// </summary>
public sealed class WindowsBalloonNotificationSender : IWindowsNotificationSender
{
    private readonly TrayIconHost trayIconHost;

    /// <summary>
    /// 通知表示に使用する共有タスクトレイアイコンを受け取ります。
    /// </summary>
    /// <param name="trayIconHost">共有タスクトレイアイコンです。</param>
    public WindowsBalloonNotificationSender(TrayIconHost trayIconHost)
    {
        ArgumentNullException.ThrowIfNull(trayIconHost);
        this.trayIconHost = trayIconHost;
    }

    /// <summary>
    /// WPFのUIスレッド上でWindowsバルーン通知を表示します。
    /// </summary>
    /// <param name="message">表示する通知内容です。</param>
    /// <param name="cancellationToken">送信のキャンセル通知です。</param>
    /// <returns>表示要求の完了を表す非同期処理です。</returns>
    public async Task SendAsync(
        WindowsNotificationMessage message,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        System.Windows.Threading.Dispatcher? dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            trayIconHost.ShowNotification(message.Title, message.Body);
            return;
        }

        await dispatcher.InvokeAsync(
            () => trayIconHost.ShowNotification(message.Title, message.Body),
            System.Windows.Threading.DispatcherPriority.Normal,
            cancellationToken);
    }
}
