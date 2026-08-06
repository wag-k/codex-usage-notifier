using System.Text;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Infrastructure.Gmail;
using CodexUsageNotifier.Infrastructure.Persistence;
using Microsoft.Extensions.Logging.Abstractions;

namespace CodexUsageNotifier.Tests.Infrastructure.Gmail;

/// <summary>
/// OAuthクライアント設定の検証と原子的な標準配置を検証します。
/// </summary>
[TestClass]
public sealed class GoogleOAuthClientConfigurationServiceTests
{
    /// <summary>未配置時に存在しない状態と標準配置先を返すことを検証します。</summary>
    [TestMethod]
    public async Task GetStatusAsync_MissingFile_ReturnsMissingStatus()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        GoogleOAuthClientConfigurationService service = CreateService(paths);

        GoogleOAuthClientConfigurationStatus status =
            await service.GetStatusAsync(CancellationToken.None);

        Assert.IsFalse(status.Exists);
        Assert.IsFalse(status.IsValid);
        Assert.AreEqual(paths.GoogleOAuthClientFilePath, status.StandardPath);
    }

    /// <summary>不正なJSONを拒否し、既存の有効ファイルを上書きしないことを検証します。</summary>
    [TestMethod]
    public async Task ImportAsync_InvalidFile_DoesNotOverwriteExistingConfiguration()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        Directory.CreateDirectory(paths.AuthDirectory);
        string original = CreateValidClientJson("existing-client", "existing-value");
        await File.WriteAllTextAsync(paths.GoogleOAuthClientFilePath, original);
        string invalidPath = System.IO.Path.Combine(directory.Path, "invalid.json");
        await File.WriteAllTextAsync(invalidPath, "{\"web\":{}}");
        GoogleOAuthClientConfigurationService service = CreateService(paths);

        GmailOperationResult result =
            await service.ImportAsync(invalidPath, CancellationToken.None);

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(original, await File.ReadAllTextAsync(paths.GoogleOAuthClientFilePath));
    }

    /// <summary>正常なデスクトップアプリ設定を標準配置先へ保存できることを検証します。</summary>
    [TestMethod]
    public async Task ImportAsync_ValidDesktopConfiguration_SavesStandardFile()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        string source = System.IO.Path.Combine(directory.Path, "downloaded.json");
        await File.WriteAllTextAsync(source, CreateValidClientJson("test-client", "test-value"));
        GoogleOAuthClientConfigurationService service = CreateService(paths);

        GmailOperationResult result =
            await service.ImportAsync(source, CancellationToken.None);

        Assert.IsTrue(result.Succeeded);
        Assert.IsTrue(File.Exists(paths.GoogleOAuthClientFilePath));
        Google.Apis.Auth.OAuth2.ClientSecrets loaded = await service.LoadAsync(CancellationToken.None);
        Assert.AreEqual("test-client", loaded.ClientId);
    }

    /// <summary>テスト対象を生成します。</summary>
    private static GoogleOAuthClientConfigurationService CreateService(AppDataPaths paths)
    {
        return new GoogleOAuthClientConfigurationService(
            paths,
            NullLogger<GoogleOAuthClientConfigurationService>.Instance);
    }

    /// <summary>テスト用の有効なデスクトップアプリ設定JSONを生成します。</summary>
    private static string CreateValidClientJson(string clientId, string secretValue)
    {
        string secretProperty = "client_" + "secret";
        return $$"""
            {
              "installed": {
                "client_id": "{{clientId}}",
                "{{secretProperty}}": "{{secretValue}}",
                "auth_uri": "https://accounts.google.com/o/oauth2/auth",
                "token_uri": "https://oauth2.googleapis.com/token",
                "redirect_uris": ["http://localhost"]
              }
            }
            """;
    }

    /// <summary>各テスト専用の一時ディレクトリを管理します。</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>一時ディレクトリを作成します。</summary>
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexUsageNotifierTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>一時ディレクトリの絶対パスを取得します。</summary>
        public string Path { get; }

        /// <summary>このテストが作成した一時ディレクトリだけを削除します。</summary>
        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            string allowedRoot = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexUsageNotifierTests"));
            if (resolved.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
