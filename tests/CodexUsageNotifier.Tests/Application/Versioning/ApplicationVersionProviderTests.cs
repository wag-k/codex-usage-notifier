using CodexUsageNotifier.Application.Versioning;

namespace CodexUsageNotifier.Tests.Application.Versioning;

/// <summary>
/// Assembly由来のRelease Version解決を検証します。
/// </summary>
[TestClass]
public sealed class ApplicationVersionProviderTests
{
    /// <summary>実行Assemblyから空でないRelease Versionを取得できることを検証します。</summary>
    [TestMethod]
    public void Constructor_CurrentAssembly_ReturnsNonEmptyVersion()
    {
        ApplicationVersionProvider provider = new();

        Assert.IsFalse(string.IsNullOrWhiteSpace(provider.Version));
    }

    /// <summary>Release指定値を保持し、ビルドメタデータだけをApp Server用の値から除くことを検証します。</summary>
    [TestMethod]
    public void Constructor_ReleaseInformationalVersion_ReturnsSpecifiedVersion()
    {
        ApplicationVersionProvider provider = new("1.2.3+abcdef0");

        Assert.AreEqual("1.2.3", provider.Version);
        Assert.AreEqual("1.2.3+abcdef0", provider.InformationalVersion);
    }
}
