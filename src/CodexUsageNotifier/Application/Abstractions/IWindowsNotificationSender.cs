using CodexUsageNotifier.Application.Notifications;

namespace CodexUsageNotifier.Application.Abstractions;

/// <summary>
/// Windowsのユーザー通知を送信する処理を抽象化します。
/// </summary>
public interface IWindowsNotificationSender
{
    /// <summary>
    /// 指定されたWindows通知を送信します。
    /// </summary>
    /// <param name="message">表示する通知内容です。</param>
    /// <param name="cancellationToken">送信のキャンセル通知です。</param>
    /// <returns>送信処理を表す非同期処理です。</returns>
    Task SendAsync(WindowsNotificationMessage message, CancellationToken cancellationToken);
}
