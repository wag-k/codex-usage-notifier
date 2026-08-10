namespace CodexUsageNotifier.Infrastructure.Startup;

/// <summary>
/// Windowsユーザー固有のLocalAppDataにあるファイルを排他保持し、アプリケーションの多重起動を防止します。
/// </summary>
public sealed class ApplicationInstanceGuard : IDisposable
{
    private FileStream? lockStream;

    /// <summary>排他所有権を取得したファイルストリームを受け取ります。</summary>
    private ApplicationInstanceGuard(FileStream lockStream)
    {
        ArgumentNullException.ThrowIfNull(lockStream);
        this.lockStream = lockStream;
    }

    /// <summary>
    /// 指定したロックファイルを排他的に開き、単一インスタンスの所有権を取得します。
    /// </summary>
    /// <param name="lockFilePath">ユーザー固有領域にあるロックファイルのパスです。</param>
    /// <param name="guard">取得成功時に解放責務を持つガードが設定されます。</param>
    /// <returns>最初のインスタンスとして所有権を取得できた場合はtrueです。</returns>
    public static bool TryAcquire(string lockFilePath, out ApplicationInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockFilePath);
        string fullPath = Path.GetFullPath(lockFilePath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidOperationException("ロックファイルの保存先を解決できません。"));

        try
        {
            FileStream stream = new(
                fullPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None);
            guard = new ApplicationInstanceGuard(stream);
            return true;
        }
        catch (IOException)
        {
            guard = null;
            return false;
        }
    }

    /// <summary>ロックファイルの排他ハンドルを解放します。</summary>
    public void Dispose()
    {
        FileStream? ownedStream = Interlocked.Exchange(ref lockStream, null);
        ownedStream?.Dispose();
    }
}
