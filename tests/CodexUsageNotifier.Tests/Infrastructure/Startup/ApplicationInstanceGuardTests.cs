using CodexUsageNotifier.Infrastructure.Startup;

namespace CodexUsageNotifier.Tests.Infrastructure.Startup;

/// <summary>
/// Windowsユーザー単位の単一起動Mutexと起動副作用の遮断を検証します。
/// </summary>
[TestClass]
public sealed class ApplicationInstanceGuardTests
{
    /// <summary>同じ名前では最初のインスタンスだけが所有権を取得できることを検証します。</summary>
    [TestMethod]
    public void TryAcquire_SameName_RejectsSecondInstance()
    {
        string mutexName = CreateUniqueMutexName();
        Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(mutexName, out ApplicationInstanceGuard? first));
        using (first)
        {
            Assert.IsFalse(ApplicationInstanceGuard.TryAcquire(mutexName, out ApplicationInstanceGuard? second));
            Assert.IsNull(second);
        }
    }

    /// <summary>最初の所有者が終了すると次のインスタンスが取得できることを検証します。</summary>
    [TestMethod]
    public void TryAcquire_AfterOwnerDisposal_AllowsNextInstance()
    {
        string mutexName = CreateUniqueMutexName();
        Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(mutexName, out ApplicationInstanceGuard? first));
        first!.Dispose();

        Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(mutexName, out ApplicationInstanceGuard? next));
        next!.Dispose();
    }

    /// <summary>二重起動側が監視・App Server・状態書込みの起動経路へ進まないことを検証します。</summary>
    [TestMethod]
    public void TryAcquire_RejectedInstance_DoesNotRunStartupSideEffects()
    {
        string mutexName = CreateUniqueMutexName();
        Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(mutexName, out ApplicationInstanceGuard? first));
        using (first)
        {
            int monitoringStarts = 0;
            int appServerStarts = 0;
            int stateWrites = 0;

            bool started = RunGuardedStartup(
                mutexName,
                () =>
                {
                    monitoringStarts++;
                    appServerStarts++;
                    stateWrites++;
                });

            Assert.IsFalse(started);
            Assert.AreEqual(0, monitoringStarts);
            Assert.AreEqual(0, appServerStarts);
            Assert.AreEqual(0, stateWrites);
        }
    }

    /// <summary>異なるWindowsユーザー識別子から異なる安全なMutex名を生成することを検証します。</summary>
    [TestMethod]
    public void CreateMutexName_DifferentUsers_CreatesDifferentNames()
    {
        string first = ApplicationInstanceGuard.CreateMutexName("S-1-5-21-test-user-1");
        string second = ApplicationInstanceGuard.CreateMutexName("S-1-5-21-test-user-2");

        Assert.AreNotEqual(first, second);
        StringAssert.StartsWith(first, "Local\\CodexUsageNotifier-");
        Assert.IsFalse(first.Contains("test-user", StringComparison.Ordinal));
    }

    /// <summary>ガード取得成功時だけ後続の起動副作用を実行します。</summary>
    private static bool RunGuardedStartup(string mutexName, Action startupSideEffects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mutexName);
        ArgumentNullException.ThrowIfNull(startupSideEffects);
        if (!ApplicationInstanceGuard.TryAcquire(mutexName, out ApplicationInstanceGuard? guard))
        {
            return false;
        }

        using (guard)
        {
            startupSideEffects();
        }

        return true;
    }

    /// <summary>テストごとに衝突しないLocal Mutex名を生成します。</summary>
    private static string CreateUniqueMutexName()
    {
        return $"Local\\CodexUsageNotifier.Tests-{Guid.NewGuid():N}";
    }
}
