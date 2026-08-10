using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace CodexUsageNotifier.Infrastructure.Startup;

/// <summary>
/// Windowsユーザーごとの名前付きMutexを所有し、アプリケーションの多重起動を防止します。
/// </summary>
public sealed class ApplicationInstanceGuard : IDisposable
{
    private Mutex? mutex;

    /// <summary>所有権を取得した名前付きMutexを受け取ります。</summary>
    private ApplicationInstanceGuard(Mutex mutex)
    {
        ArgumentNullException.ThrowIfNull(mutex);
        this.mutex = mutex;
    }

    /// <summary>
    /// 現在のWindowsユーザー専用Mutexを取得します。
    /// </summary>
    /// <param name="guard">取得成功時に解放責務を持つガードが設定されます。</param>
    /// <returns>最初のインスタンスとして所有権を取得できた場合はtrueです。</returns>
    public static bool TryAcquireForCurrentUser(out ApplicationInstanceGuard? guard)
    {
        return TryAcquire(CreateMutexName(GetCurrentUserIdentifier()), out guard);
    }

    /// <summary>
    /// 指定名のMutexを最初に作成できた場合だけ所有します。
    /// </summary>
    /// <param name="mutexName">取得する名前付きMutexの名前です。</param>
    /// <param name="guard">取得成功時に解放責務を持つガードが設定されます。</param>
    /// <returns>所有権を取得できた場合はtrueです。</returns>
    internal static bool TryAcquire(string mutexName, out ApplicationInstanceGuard? guard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        Mutex candidate = new(initiallyOwned: true, mutexName, out bool createdNew);
        if (!createdNew)
        {
            candidate.Dispose();
            guard = null;
            return false;
        }

        guard = new ApplicationInstanceGuard(candidate);
        return true;
    }

    /// <summary>
    /// Windowsユーザー識別子を名前へ直接露出しないMutex名へ変換します。
    /// </summary>
    /// <param name="userIdentifier">SIDなどのユーザー固有識別子です。</param>
    /// <returns>ユーザーごとに異なるLocal名前空間のMutex名です。</returns>
    internal static string CreateMutexName(string userIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userIdentifier);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(userIdentifier));
        return $"Local\\CodexUsageNotifier-{Convert.ToHexString(hash.AsSpan(0, 16))}";
    }

    /// <summary>現在のWindowsユーザーを安定して識別する値を取得します。</summary>
    private static string GetCurrentUserIdentifier()
    {
        string? sid = WindowsIdentity.GetCurrent().User?.Value;
        return string.IsNullOrWhiteSpace(sid)
            ? $"{Environment.UserDomainName}\\{Environment.UserName}"
            : sid;
    }

    /// <summary>Mutexの所有権とOSハンドルを解放します。</summary>
    public void Dispose()
    {
        Mutex? ownedMutex = Interlocked.Exchange(ref mutex, null);
        if (ownedMutex is null)
        {
            return;
        }

        try
        {
            ownedMutex.ReleaseMutex();
        }
        finally
        {
            ownedMutex.Dispose();
        }
    }
}
