using System.Diagnostics;
using System.Text;
using CodexUsageNotifier.Infrastructure.Startup;

namespace CodexUsageNotifier.Tests.Infrastructure.Startup;

/// <summary>
/// Windowsユーザー固有領域のファイルロックと起動副作用の遮断を検証します。
/// </summary>
[TestClass]
public sealed class ApplicationInstanceGuardTests
{
    /// <summary>同じロックパスでは最初のインスタンスだけが所有権を取得できることを検証します。</summary>
    [TestMethod]
    public void TryAcquire_SamePath_RejectsSecondInstance()
    {
        using TemporaryDirectory directory = new();
        string lockPath = Path.Combine(directory.Path, "instance.lock");
        Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(lockPath, out ApplicationInstanceGuard? first));
        using (first)
        {
            Assert.IsFalse(ApplicationInstanceGuard.TryAcquire(lockPath, out ApplicationInstanceGuard? second));
            Assert.IsNull(second);
        }
    }

    /// <summary>最初の所有者が終了すると次のインスタンスが取得できることを検証します。</summary>
    [TestMethod]
    public void TryAcquire_AfterOwnerDisposal_AllowsNextInstance()
    {
        using TemporaryDirectory directory = new();
        string lockPath = Path.Combine(directory.Path, "instance.lock");
        Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(lockPath, out ApplicationInstanceGuard? first));
        first!.Dispose();

        Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(lockPath, out ApplicationInstanceGuard? next));
        next!.Dispose();
    }

    /// <summary>残存ロックファイルは所有者不在なら次回起動を妨げないことを検証します。</summary>
    [TestMethod]
    public void TryAcquire_PreexistingUnlockedFile_AllowsInstance()
    {
        using TemporaryDirectory directory = new();
        string lockPath = Path.Combine(directory.Path, "instance.lock");
        File.WriteAllText(lockPath, string.Empty);

        Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(lockPath, out ApplicationInstanceGuard? guard));
        guard!.Dispose();
    }

    /// <summary>別プロセスの異常終了後にOSがファイルハンドルを解放することを検証します。</summary>
    [TestMethod]
    public async Task TryAcquire_AfterOwnerProcessKilled_AllowsNextInstance()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windowsファイル共有ロックの統合テストです。");
        }

        using TemporaryDirectory directory = new();
        string lockPath = Path.Combine(directory.Path, "instance.lock");
        using Process ownerProcess = CreateLockOwnerProcess(lockPath);
        try
        {
            Assert.IsTrue(ownerProcess.Start());
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
            string? ready = await ownerProcess.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.AreEqual("READY", ready);
            Assert.IsFalse(ApplicationInstanceGuard.TryAcquire(lockPath, out ApplicationInstanceGuard? blocked));
            Assert.IsNull(blocked);

            ownerProcess.Kill(entireProcessTree: true);
            await ownerProcess.WaitForExitAsync(timeout.Token);

            Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(lockPath, out ApplicationInstanceGuard? recovered));
            recovered!.Dispose();
        }
        finally
        {
            if (!ownerProcess.HasExited)
            {
                ownerProcess.Kill(entireProcessTree: true);
                await ownerProcess.WaitForExitAsync();
            }
        }
    }

    /// <summary>二重起動側が監視・App Server・状態書込みの起動経路へ進まないことを検証します。</summary>
    [TestMethod]
    public void TryAcquire_RejectedInstance_DoesNotRunStartupSideEffects()
    {
        using TemporaryDirectory directory = new();
        string lockPath = Path.Combine(directory.Path, "instance.lock");
        Assert.IsTrue(ApplicationInstanceGuard.TryAcquire(lockPath, out ApplicationInstanceGuard? first));
        using (first)
        {
            int sideEffectCount = 0;
            bool started = RunGuardedStartup(lockPath, () => sideEffectCount++);

            Assert.IsFalse(started);
            Assert.AreEqual(0, sideEffectCount);
        }
    }

    /// <summary>ガード取得成功時だけ後続の起動副作用を実行します。</summary>
    private static bool RunGuardedStartup(string lockPath, Action startupSideEffects)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        ArgumentNullException.ThrowIfNull(startupSideEffects);
        if (!ApplicationInstanceGuard.TryAcquire(lockPath, out ApplicationInstanceGuard? guard))
        {
            return false;
        }

        using (guard)
        {
            startupSideEffects();
        }

        return true;
    }

    /// <summary>指定ファイルを排他保持したまま待機するWindows子プロセスを生成します。</summary>
    private static Process CreateLockOwnerProcess(string lockPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lockPath);
        string escapedPath = lockPath.Replace("'", "''", StringComparison.Ordinal);
        string script = "$stream = [System.IO.File]::Open('" + escapedPath
            + "', 'OpenOrCreate', 'ReadWrite', 'None'); "
            + "[Console]::Out.WriteLine('READY'); [Console]::Out.Flush(); "
            + "[System.Threading.Thread]::Sleep([System.Threading.Timeout]::Infinite)";
        string encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-EncodedCommand");
        startInfo.ArgumentList.Add(encodedScript);
        return new Process { StartInfo = startInfo };
    }
}
