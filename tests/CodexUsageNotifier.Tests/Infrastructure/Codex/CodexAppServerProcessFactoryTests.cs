using CodexUsageNotifier.Infrastructure.Codex;

namespace CodexUsageNotifier.Tests.Infrastructure.Codex;

/// <summary>
/// Windows上のCodex CLI解決とApp Server起動情報を検証します。
/// </summary>
[TestClass]
public sealed class CodexAppServerProcessFactoryTests
{
    /// <summary>
    /// 拡張子なしのcodex設定からPATH上のcodex.cmdを解決できることを検証します。
    /// </summary>
    [TestMethod]
    public void ResolveExecutablePath_CommandName_ResolvesCmdFromPath()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"codex-process-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string expected = Path.Combine(directory, "codex.cmd");
        try
        {
            File.WriteAllText(expected, "@echo off");

            string actual = CodexAppServerProcessFactory.ResolveExecutablePath(
                "codex",
                directory,
                ".EXE;.CMD");

            Assert.AreEqual(expected, actual, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// cmdファイルはcmd.exe経由で起動し、JSON-RPC用の標準入出力をリダイレクトすることを検証します。
    /// </summary>
    [TestMethod]
    public void CreateStartInfo_CmdFile_UsesCommandInterpreter()
    {
        const string executablePath = @"C:\Program Files\Codex\codex.cmd";

        System.Diagnostics.ProcessStartInfo result =
            CodexAppServerProcessFactory.CreateStartInfo(executablePath);

        Assert.AreEqual("cmd.exe", Path.GetFileName(result.FileName), ignoreCase: true);
        CollectionAssert.AreEqual(
            new[] { "/d", "/s", "/c", $"\"{executablePath}\" app-server --listen stdio://" },
            result.ArgumentList.ToArray());
        AssertRedirectsStandardStreams(result);
    }

    /// <summary>
    /// exeファイルはシェルを介さず直接App Server引数を渡すことを検証します。
    /// </summary>
    [TestMethod]
    public void CreateStartInfo_ExeFile_StartsDirectly()
    {
        const string executablePath = @"C:\Tools\codex.exe";

        System.Diagnostics.ProcessStartInfo result =
            CodexAppServerProcessFactory.CreateStartInfo(executablePath);

        Assert.AreEqual(executablePath, result.FileName);
        CollectionAssert.AreEqual(
            new[] { "app-server", "--listen", "stdio://" },
            result.ArgumentList.ToArray());
        AssertRedirectsStandardStreams(result);
    }

    /// <summary>
    /// JSON-RPC通信に必要な標準ストリーム設定を検証します。
    /// </summary>
    /// <param name="startInfo">検証するプロセス起動情報です。</param>
    private static void AssertRedirectsStandardStreams(System.Diagnostics.ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        Assert.IsFalse(startInfo.UseShellExecute);
        Assert.IsTrue(startInfo.CreateNoWindow);
        Assert.IsTrue(startInfo.RedirectStandardInput);
        Assert.IsTrue(startInfo.RedirectStandardOutput);
        Assert.IsTrue(startInfo.RedirectStandardError);
    }
}
