namespace CodexUsageNotifier.Application.Ntfy;

/// <summary>秘密Topicからスマートフォン向けntfy購読リンクを生成します。</summary>
public static class NtfySubscriptionLinkFactory
{
    /// <summary>Androidのntfyアプリでntfy.sh購読画面を開くディープリンクを生成します。</summary>
    public static string CreateAndroidDeepLink(NtfyTopic topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        if (!NtfyTopicValidator.IsValid(topic.Value))
        {
            throw new ArgumentException("スマホ通知Topicの形式が不正です。", nameof(topic));
        }

        return $"ntfy://ntfy.sh/{Uri.EscapeDataString(topic.Value)}?display=Codex%20Usage%20Notifier";
    }
}
