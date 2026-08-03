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
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
