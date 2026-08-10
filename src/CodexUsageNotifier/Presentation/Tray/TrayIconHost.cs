using Forms = System.Windows.Forms;

namespace CodexUsageNotifier.Presentation.Tray;

/// <summary>
/// タスクトレイアイコンを共有し、メニュー表示とWindowsバルーン通知を提供します。
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private const string IconResourceName = "CodexUsageNotifier.AppIcon.ico";

    private Forms.NotifyIcon? notifyIcon;
    private System.Drawing.Icon? applicationIcon;
    private bool disposed;

    /// <summary>
    /// バルーン通知がクリックされたときに発生します。
    /// </summary>
    public event EventHandler? NotificationClicked;

    /// <summary>
    /// タスクトレイアイコンを生成し、指定されたメニューとダブルクリック処理を設定します。
    /// </summary>
    /// <param name="menu">タスクトレイへ表示するメニューです。</param>
    /// <param name="doubleClickHandler">アイコンのダブルクリック処理です。</param>
    public void Initialize(Forms.ContextMenuStrip menu, EventHandler doubleClickHandler)
    {
        ArgumentNullException.ThrowIfNull(menu);
        ArgumentNullException.ThrowIfNull(doubleClickHandler);
        ObjectDisposedException.ThrowIf(disposed, this);
        if (notifyIcon is not null)
        {
            return;
        }

        applicationIcon ??= LoadApplicationIcon();
        notifyIcon = new Forms.NotifyIcon
        {
            Text = "Codex Usage Notifier",
            Icon = applicationIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        notifyIcon.DoubleClick += doubleClickHandler;
        notifyIcon.BalloonTipClicked += OnBalloonTipClicked;
    }

    /// <summary>
    /// 共有トレイアイコンからWindowsバルーン通知を表示します。
    /// </summary>
    /// <param name="title">通知タイトルです。</param>
    /// <param name="body">通知本文です。</param>
    public void ShowNotification(string title, string body)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(body);
        ObjectDisposedException.ThrowIf(disposed, this);
        Forms.NotifyIcon icon = notifyIcon
            ?? throw new InvalidOperationException("タスクトレイが初期化されていません。");
        icon.BalloonTipTitle = title;
        icon.BalloonTipText = body;
        icon.BalloonTipIcon = Forms.ToolTipIcon.Info;
        icon.ShowBalloonTip(10000);
    }

    /// <summary>
    /// 埋め込みリソースからアプリケーションアイコンを読み込みます。
    /// </summary>
    /// <returns>呼び出し元が破棄するアプリケーションアイコンです。</returns>
    private static System.Drawing.Icon LoadApplicationIcon()
    {
        using Stream stream = typeof(TrayIconHost).Assembly.GetManifestResourceStream(IconResourceName)
            ?? throw new InvalidOperationException("アプリケーションアイコンを読み込めませんでした。");
        using System.Drawing.Icon resourceIcon = new(stream);
        return (System.Drawing.Icon)resourceIcon.Clone();
    }

    /// <summary>
    /// バルーン通知のクリックを購読者へ転送します。
    /// </summary>
    /// <param name="sender">イベント送信元です。</param>
    /// <param name="e">イベント引数です。</param>
    private void OnBalloonTipClicked(object? sender, EventArgs e)
    {
        NotificationClicked?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// タスクトレイアイコンを非表示にして破棄します。
    /// </summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (notifyIcon is not null)
        {
            notifyIcon.BalloonTipClicked -= OnBalloonTipClicked;
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
            notifyIcon = null;
        }

        applicationIcon?.Dispose();
        applicationIcon = null;
    }
}
