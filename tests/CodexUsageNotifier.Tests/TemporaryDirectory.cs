namespace CodexUsageNotifier.Tests;

/// <summary>
/// 単体テストごとに分離された一時ディレクトリを管理します。
/// </summary>
internal sealed class TemporaryDirectory : IDisposable
{
    /// <summary>
    /// システム一時領域の配下にテスト専用ディレクトリを作成します。
    /// </summary>
    internal TemporaryDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "CodexUsageNotifier.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    /// <summary>
    /// テスト専用ディレクトリの絶対パスを取得します。
    /// </summary>
    internal string Path { get; }

    /// <summary>
    /// テストで作成したファイルとディレクトリを削除します。
    /// </summary>
    public void Dispose()
    {
        const int maximumAttempts = 10;
        for (int attempt = 1; attempt <= maximumAttempts; attempt++)
        {
            if (!Directory.Exists(Path))
            {
                return;
            }

            try
            {
                Directory.Delete(Path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < maximumAttempts)
            {
                // Windowsでは終了直後の子プロセスがファイルハンドルを解放するまで短い遅延が生じる場合があります。
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }
            catch (UnauthorizedAccessException) when (attempt < maximumAttempts)
            {
                // ウイルス対策ソフトなどによる一時的な走査中も、短時間だけ削除を再試行します。
                Thread.Sleep(TimeSpan.FromMilliseconds(100));
            }
        }
    }
}
