using System.Text;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Application.Ntfy;
using CodexUsageNotifier.Domain.Models;
using CodexUsageNotifier.Infrastructure.Gmail;
using CodexUsageNotifier.Infrastructure.Ntfy;
using CodexUsageNotifier.Infrastructure.Persistence;

namespace CodexUsageNotifier.Tests.Infrastructure.Ntfy;

/// <summary>匿名ntfy Topicの生成品質と暗号化永続化を検証します。</summary>
[TestClass]
public sealed class NtfyTopicSecurityTests
{
    /// <summary>Topicが安全な文字、64文字以内、160ビット乱数、重複なしで生成されることを検証します。</summary>
    [TestMethod]
    public void Generate_CryptographicTopics_AreSafeAndUnique()
    {
        CryptographicNtfyTopicGenerator generator = new();
        NtfyTopic[] topics = Enumerable.Range(0, 100).Select(_ => generator.Generate()).ToArray();

        Assert.AreEqual(100, topics.Select(item => item.Value).Distinct(StringComparer.Ordinal).Count());
        Assert.IsTrue(topics.All(item => item.Value.StartsWith("codex-usage-", StringComparison.Ordinal)));
        Assert.IsTrue(topics.All(item => item.Value.Length <= 64));
        Assert.IsTrue(topics.All(item => item.Value.All(character => char.IsAsciiLetterOrDigit(character) || character == '-')));
        Assert.IsTrue(topics.All(item => item.Value["codex-usage-".Length..].Length == 40));
    }

    /// <summary>秘密Topicが平文で保存されず、同じユーザー保護処理で読み戻せることを検証します。</summary>
    [TestMethod]
    public async Task SaveAsync_ProtectedStore_RoundTripsWithoutPlaintext()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        using DpapiNtfyTopicStore store = new(paths, new XorProtector());
        NtfyTopic expected = new() { Value = "codex-usage-secret-marker", GenerationId = "generation-1", CreatedAtUtc = DateTimeOffset.UtcNow };

        await store.SaveAsync(expected, CancellationToken.None);
        NtfyTopic? actual = await store.LoadAsync(CancellationToken.None);
        string saved = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(paths.NtfyTopicFilePath));

        Assert.AreEqual(expected, actual);
        Assert.IsFalse(saved.Contains(expected.Value, StringComparison.Ordinal));
        Assert.IsFalse(saved.TrimStart().StartsWith('{'));
    }

    /// <summary>破損したTopicファイルを秘密値を含まない安全なエラーとして扱うことを検証します。</summary>
    [TestMethod]
    public async Task LoadAsync_CorruptedTopic_ThrowsSafeError()
    {
        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        Directory.CreateDirectory(paths.AuthDirectory);
        await File.WriteAllBytesAsync(paths.NtfyTopicFilePath, [1, 2, 3]);
        using DpapiNtfyTopicStore store = new(paths, new XorProtector());

        await Assert.ThrowsExceptionAsync<InvalidDataException>(() => store.LoadAsync(CancellationToken.None));
    }

    /// <summary>WindowsではCurrentUser DPAPIでTopicを実際に保存・復号できることを検証します。</summary>
    [TestMethod]
    public async Task WindowsUserDataProtector_CurrentUser_RoundTripsTopic()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Windows DPAPI専用の統合テストです。");
        }

        using TemporaryDirectory directory = new();
        AppDataPaths paths = new(directory.Path);
        using DpapiNtfyTopicStore store = new(paths, new WindowsUserDataProtector());
        NtfyTopic expected = new()
        {
            Value = "codex-usage-0123456789abcdef0123456789abcdef",
            GenerationId = "dpapi-generation",
            CreatedAtUtc = DateTimeOffset.UtcNow,
        };

        await store.SaveAsync(expected, CancellationToken.None);
        NtfyTopic? actual = await store.LoadAsync(CancellationToken.None);

        Assert.AreEqual(expected, actual);
    }

    /// <summary>設定・状態モデルが秘密Topic本体を保持するプロパティを持たないことを検証します。</summary>
    [TestMethod]
    public void PersistentPublicModels_DoNotExposePlaintextTopicProperty()
    {
        string[] forbiddenNames = ["NtfyTopic", "NtfyTopicValue", "TopicValue"];

        Assert.IsFalse(typeof(AppSettings).GetProperties().Any(property => forbiddenNames.Contains(property.Name, StringComparer.Ordinal)));
        Assert.IsFalse(typeof(ApplicationState).GetProperties().Any(property => forbiddenNames.Contains(property.Name, StringComparer.Ordinal)));
        Assert.IsFalse(typeof(RateLimitNotificationState).GetProperties().Any(property => forbiddenNames.Contains(property.Name, StringComparer.Ordinal)));
    }

    /// <summary>テスト専用の可逆変換で平文保存を検出可能にします。</summary>
    private sealed class XorProtector : IUserDataProtector
    {
        /// <inheritdoc />
        public byte[] Protect(byte[] plaintext) => Transform(plaintext);

        /// <inheritdoc />
        public byte[] Unprotect(byte[] protectedData) => Transform(protectedData);

        /// <summary>各バイトを固定値で反転します。</summary>
        private static byte[] Transform(byte[] source)
        {
            ArgumentNullException.ThrowIfNull(source);
            return source.Select(value => (byte)(value ^ 0xA5)).ToArray();
        }
    }
}
