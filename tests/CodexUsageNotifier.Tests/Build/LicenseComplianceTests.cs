using System.Diagnostics;
using System.Text.Json;

namespace CodexUsageNotifier.Tests.Build;

/// <summary>
/// リポジトリと配布ビルドのライセンス監査基盤を検証します。
/// </summary>
[TestClass]
public sealed class LicenseComplianceTests
{
    private static readonly string RepositoryRoot = FindRepositoryRoot();

    /// <summary>ルートLICENSEが標準MIT本文と指定著作権表示を保持することを検証します。</summary>
    [TestMethod]
    public void License_ContainsStandardMitTextAndCopyright()
    {
        string content = File.ReadAllText(Path.Combine(RepositoryRoot, "LICENSE"));

        StringAssert.StartsWith(content, "MIT License");
        StringAssert.Contains(content, "Copyright (c) 2026 Kenta Kawaguchi");
        StringAssert.Contains(content, "Permission is hereby granted, free of charge");
        StringAssert.Contains(content, "THE SOFTWARE IS PROVIDED \"AS IS\"");
    }

    /// <summary>第三者通知と監査マニフェストが空でなく、配布範囲を区別することを検証します。</summary>
    [TestMethod]
    public void ThirdPartyNotices_ManifestSeparatesRuntimeAndTestDependencies()
    {
        string noticePath = Path.Combine(RepositoryRoot, "THIRD-PARTY-NOTICES.txt");
        FileInfo notice = new(noticePath);
        Assert.IsTrue(notice.Exists);
        Assert.IsTrue(notice.Length > 0);

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(
            Path.Combine(RepositoryRoot, "eng", "licenses-audit.json")));
        JsonElement root = document.RootElement;
        JsonElement[] runtimePackages = root.GetProperty("runtimePackages").EnumerateArray().ToArray();
        JsonElement[] testPackages = root.GetProperty("buildAndTestOnlyPackages").EnumerateArray().ToArray();
        Assert.IsTrue(runtimePackages.Length > 0);
        Assert.IsTrue(testPackages.Length > 0);
        Assert.IsTrue(runtimePackages.All(item => item.GetProperty("distributedInRelease").GetBoolean()));
        Assert.IsTrue(testPackages.All(item => !item.GetProperty("distributedInRelease").GetBoolean()));
        Assert.IsTrue(runtimePackages.Any(item => item.GetProperty("dependencyType").GetString() == "Direct"));
        Assert.IsTrue(runtimePackages.Any(item => item.GetProperty("dependencyType").GetString() == "Transitive"));
    }

    /// <summary>現在のlock file、NuGet metadata、runtime packが監査マニフェストと一致することを検証します。</summary>
    [TestMethod]
    public async Task AuditLicenses_CurrentDependencyGraph_Succeeds()
    {
        ProcessResult result = await RunAuditAsync(
            Path.Combine(RepositoryRoot, "eng", "licenses-audit.json"));

        Assert.AreEqual(0, result.ExitCode, result.Output);
        StringAssert.Contains(result.Output, "License audit passed");
    }

    /// <summary>publish先へ本体と依存元のライセンスファイルを配置できることを検証します。</summary>
    [TestMethod]
    public async Task AuditLicenses_PublishDirectory_CopiesRequiredLicenseFiles()
    {
        using TemporaryDirectory directory = new();

        ProcessResult result = await RunAuditAsync(
            Path.Combine(RepositoryRoot, "eng", "licenses-audit.json"),
            directory.Path);

        Assert.AreEqual(0, result.ExitCode, result.Output);
        Assert.IsTrue(new FileInfo(Path.Combine(directory.Path, "LICENSE")).Length > 0);
        Assert.IsTrue(new FileInfo(Path.Combine(directory.Path, "THIRD-PARTY-NOTICES.txt")).Length > 0);
        Assert.IsTrue(new FileInfo(Path.Combine(directory.Path, "licenses-audit.json")).Length > 0);
        Assert.IsTrue(Directory.EnumerateFiles(
            Path.Combine(directory.Path, "licenses", "dotnet"),
            "*",
            SearchOption.AllDirectories).Any());
    }

    /// <summary>不明またはレビュー必須のライセンスを監査が拒否することを検証します。</summary>
    /// <param name="license">拒否対象のライセンス表現です。</param>
    [DataTestMethod]
    [DataRow("Unknown")]
    [DataRow("GPL-3.0-only")]
    public async Task AuditLicenses_UnknownOrReviewRequiredLicense_Fails(string license)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(license);
        using TemporaryDirectory directory = new();
        string source = File.ReadAllText(Path.Combine(RepositoryRoot, "eng", "licenses-audit.json"));
        string modified = source.Replace(
            "\"license\": \"Apache-2.0\"",
            $"\"license\": \"{license}\"",
            StringComparison.Ordinal);
        string manifestPath = Path.Combine(directory.Path, "licenses-audit.json");
        await File.WriteAllTextAsync(manifestPath, modified);

        ProcessResult result = await RunAuditAsync(manifestPath);

        Assert.AreNotEqual(0, result.ExitCode, result.Output);
    }

    /// <summary>指定マニフェストでPowerShellライセンス監査を実行します。</summary>
    /// <param name="manifestPath">監査対象マニフェストのパスです。</param>
    /// <param name="publishDirectory">ライセンスファイルを配置するpublish先です。</param>
    /// <returns>終了コードと安全な標準出力です。</returns>
    private static async Task<ProcessResult> RunAuditAsync(
        string manifestPath,
        string? publishDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        ProcessStartInfo startInfo = new("powershell.exe")
        {
            WorkingDirectory = RepositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(RepositoryRoot, "eng", "Audit-Licenses.ps1"));
        startInfo.ArgumentList.Add("-ManifestPath");
        startInfo.ArgumentList.Add(manifestPath);
        if (!string.IsNullOrWhiteSpace(publishDirectory))
        {
            startInfo.ArgumentList.Add("-PublishDirectory");
            startInfo.ArgumentList.Add(publishDirectory);
        }
        using Process process = new() { StartInfo = startInfo };
        Assert.IsTrue(process.Start());
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        return new ProcessResult(
            process.ExitCode,
            string.Concat(await standardOutput, Environment.NewLine, await standardError));
    }

    /// <summary>ソリューションファイルを基準にリポジトリルートを探索します。</summary>
    /// <returns>リポジトリルートの絶対パスです。</returns>
    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "CodexUsageNotifier.sln")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new InvalidOperationException("テスト実行位置からリポジトリルートを特定できません。");
    }

    /// <summary>外部監査プロセスの結果を保持します。</summary>
    /// <param name="ExitCode">プロセス終了コードです。</param>
    /// <param name="Output">標準出力と標準エラーを結合した内容です。</param>
    private sealed record ProcessResult(int ExitCode, string Output);
}
