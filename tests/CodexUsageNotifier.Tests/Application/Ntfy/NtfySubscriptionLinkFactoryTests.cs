using CodexUsageNotifier.Application.Ntfy;

namespace CodexUsageNotifier.Tests.Application.Ntfy;

/// <summary>ntfy購読ディープリンクをUIから独立して検証します。</summary>
[TestClass]
public sealed class NtfySubscriptionLinkFactoryTests
{
    /// <summary>ntfy.shと秘密Topicを含むAndroid向けリンクを生成することを検証します。</summary>
    [TestMethod]
    public void CreateAndroidDeepLink_ValidTopic_ReturnsNtfyScheme()
    {
        NtfyTopic topic = new()
        {
            Value = "codex-usage-0123456789abcdef0123456789abcdef",
            GenerationId = "generation",
        };

        string link = NtfySubscriptionLinkFactory.CreateAndroidDeepLink(topic);

        Assert.AreEqual(
            "ntfy://ntfy.sh/codex-usage-0123456789abcdef0123456789abcdef?display=Codex%20Usage%20Notifier",
            link);
    }
}
