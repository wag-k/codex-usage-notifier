using System.Security.Cryptography;
using System.Text;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Infrastructure.Gmail;
using CodexUsageNotifier.Infrastructure.Persistence;
using Google.Apis.Auth.OAuth2.Responses;

namespace CodexUsageNotifier.Tests.Infrastructure.Gmail;

/// <summary>
/// Gmail資格情報ストアの暗号化、破損耐性、およびDPAPI統合を検証します。
/// </summary>
[TestClass]
public sealed class DpapiGmailCredentialStoreTests
{
    /// <summary>差し替え可能な保護処理で資格情報を保存して読み戻せることを検証します。</summary>
    [TestMethod]
    public async Task StoreAsync_ProtectedStore_RoundTripsTokenAndMetadata()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        using DpapiGmailCredentialStore store = new(paths, new XorDataProtector());
        TokenResponse token = new() { AccessToken = "access-value", RefreshToken = "refresh-value" };
        GmailCredentialMetadata metadata = new()
        {
            EmailAddress = "user@example.com",
            LastAuthenticatedAtUtc = new DateTimeOffset(2026, 8, 6, 0, 0, 0, TimeSpan.Zero),
        };

        await store.StoreAsync("token", token);
        await store.SaveMetadataAsync(metadata, CancellationToken.None);

        TokenResponse? loadedToken = await store.GetAsync<TokenResponse>("token");
        GmailCredentialMetadata? loadedMetadata = await store.LoadMetadataAsync(CancellationToken.None);
        Assert.AreEqual("access-value", loadedToken?.AccessToken);
        Assert.AreEqual("refresh-value", loadedToken?.RefreshToken);
        Assert.AreEqual(metadata.EmailAddress, loadedMetadata?.EmailAddress);
    }

    /// <summary>保存ファイルが平文JSONでもトークン文字列でもないことを検証します。</summary>
    [TestMethod]
    public async Task StoreAsync_ProtectedStore_DoesNotWritePlaintextJson()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        using DpapiGmailCredentialStore store = new(paths, new XorDataProtector());

        await store.StoreAsync("token", new TokenResponse
        {
            AccessToken = "plain-access-marker",
            RefreshToken = "plain-refresh-marker",
        });

        byte[] bytes = await File.ReadAllBytesAsync(paths.GoogleCredentialFilePath);
        string fileText = Encoding.UTF8.GetString(bytes);
        Assert.IsFalse(fileText.TrimStart().StartsWith('{'));
        Assert.IsFalse(fileText.Contains("plain-access-marker", StringComparison.Ordinal));
        Assert.IsFalse(fileText.Contains("plain-refresh-marker", StringComparison.Ordinal));
    }

    /// <summary>破損した暗号文を安全な再認証例外として扱うことを検証します。</summary>
    [TestMethod]
    public async Task GetAsync_CorruptedCredential_ThrowsSafeStoreException()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        Directory.CreateDirectory(paths.AuthDirectory);
        await File.WriteAllBytesAsync(paths.GoogleCredentialFilePath, [1, 2, 3, 4]);
        using DpapiGmailCredentialStore store = new(paths, new RejectingDataProtector());

        GmailCredentialStoreException exception = await Assert.ThrowsExceptionAsync<GmailCredentialStoreException>(
            () => store.GetAsync<TokenResponse>("token"));

        StringAssert.Contains(exception.Message, "再認証");
    }

    /// <summary>WindowsではCurrentUser DPAPIで実際に保存と復号ができることを検証します。</summary>
    [TestMethod]
    public async Task WindowsUserDataProtector_CurrentUser_RoundTripsCredential()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows DPAPI専用の統合テストです。");
        }

        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        using DpapiGmailCredentialStore store = new(paths, new WindowsUserDataProtector());
        await store.StoreAsync("token", new TokenResponse { RefreshToken = "dpapi-test-value" });

        TokenResponse? loaded = await store.GetAsync<TokenResponse>("token");

        Assert.AreEqual("dpapi-test-value", loaded?.RefreshToken);
    }

    /// <summary>テスト用に単純な可逆変換を行います。</summary>
    private sealed class XorDataProtector : IUserDataProtector
    {
        /// <inheritdoc />
        public byte[] Protect(byte[] plaintext)
        {
            ArgumentNullException.ThrowIfNull(plaintext);
            return Transform(plaintext);
        }

        /// <inheritdoc />
        public byte[] Unprotect(byte[] protectedData)
        {
            ArgumentNullException.ThrowIfNull(protectedData);
            return Transform(protectedData);
        }

        /// <summary>各バイトを固定値でXORします。</summary>
        private static byte[] Transform(byte[] source)
        {
            return source.Select(value => (byte)(value ^ 0xA5)).ToArray();
        }
    }

    /// <summary>復号失敗を再現するテスト用保護処理です。</summary>
    private sealed class RejectingDataProtector : IUserDataProtector
    {
        /// <inheritdoc />
        public byte[] Protect(byte[] plaintext)
        {
            ArgumentNullException.ThrowIfNull(plaintext);
            return plaintext.ToArray();
        }

        /// <inheritdoc />
        public byte[] Unprotect(byte[] protectedData)
        {
            ArgumentNullException.ThrowIfNull(protectedData);
            throw new CryptographicException("test corruption");
        }
    }

    /// <summary>各テスト専用の安全に削除できる一時ディレクトリを管理します。</summary>
    private sealed class TemporaryDirectory : IDisposable
    {
        /// <summary>一時ディレクトリを作成します。</summary>
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexUsageNotifierTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        /// <summary>一時ディレクトリを取得します。</summary>
        public string Path { get; }

        /// <summary>このテストが作成した一時ディレクトリだけを削除します。</summary>
        public void Dispose()
        {
            string resolved = System.IO.Path.GetFullPath(Path);
            string root = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "CodexUsageNotifierTests"));
            if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
            {
                Directory.Delete(resolved, recursive: true);
            }
        }
    }
}
