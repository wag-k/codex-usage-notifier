namespace CodexUsageNotifier.Presentation;

/// <summary>
/// アプリから開くことを許可した公開ドキュメントの固定URLを提供します。
/// </summary>
public static class PublicDocumentationLinks
{
    /// <summary>GitHub上のGmail OAuth設定手順を取得します。</summary>
    public static Uri GmailOAuthSetupUri { get; } = new(
        "https://github.com/wag-k/codex-usage-notifier/blob/main/docs/gmail-oauth-setup.md",
        UriKind.Absolute);

    /// <summary>ntfy公式サイトの固定URLを取得します。</summary>
    public static Uri NtfyOfficialSiteUri { get; } = new("https://ntfy.sh/", UriKind.Absolute);
}
