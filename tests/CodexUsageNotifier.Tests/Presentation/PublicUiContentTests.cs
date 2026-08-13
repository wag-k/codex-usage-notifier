using System.Text.RegularExpressions;
using CodexUsageNotifier.Application.Gmail;
using CodexUsageNotifier.Presentation;
using CodexUsageNotifier.Presentation.ViewModels;
using CodexUsageNotifier.Tests.TestDoubles;

namespace CodexUsageNotifier.Tests.Presentation;

/// <summary>
/// 公開ベータ向け画面の案内、固定リンク、および内部用語の非表示を検証します。
/// </summary>
[TestClass]
public sealed class PublicUiContentTests
{
    /// <summary>受信メールを読み取らないことがプライバシー説明へ明記されることを検証します。</summary>
    [TestMethod]
    public void GmailPrivacyDescription_StatesReceivedMailIsNotRead()
    {
        StringAssert.Contains(GmailOnboardingContent.PrivacyDescription, "Gmailの受信メールは読み取りません");
        StringAssert.Contains(GmailOnboardingContent.PrivacyDescription, "送信に必要な権限だけ");
    }

    /// <summary>GoogleアカウントなしでもWindows通知だけで利用できる説明を検証します。</summary>
    [TestMethod]
    public void GmailOptionalDescription_StatesWindowsOnlyUsageIsAvailable()
    {
        StringAssert.Contains(GmailOnboardingContent.OptionalDescription, "Windows通知だけ");
        StringAssert.Contains(GmailOnboardingContent.OptionalDescription, "Googleアカウントを設定しなくても");
    }

    /// <summary>公開画面のXAMLにPhase 4B表記が残っていないことを検証します。</summary>
    [TestMethod]
    public void PublicXaml_DoesNotContainPhase4B()
    {
        Assert.IsFalse(ReadPublicXaml().Contains("Phase 4B", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>公開画面のXAMLにPhase 4C表記が残っていないことを検証します。</summary>
    [TestMethod]
    public void PublicXaml_DoesNotContainPhase4C()
    {
        Assert.IsFalse(ReadPublicXaml().Contains("Phase 4C", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>公開画面のXAMLにその他の開発Phase番号が残っていないことを検証します。</summary>
    [TestMethod]
    public void PublicXaml_DoesNotContainAnyNumberedPhase()
    {
        Assert.IsFalse(Regex.IsMatch(ReadPublicXaml(), @"Phase\s*\d", RegexOptions.IgnoreCase));
    }

    /// <summary>Gmail設定手順が固定されたHTTPSのGitHub URLだけを使用することを検証します。</summary>
    [TestMethod]
    public void GmailOAuthSetupLink_UsesFixedTrustedHttpsUrl()
    {
        Uri uri = PublicDocumentationLinks.GmailOAuthSetupUri;

        Assert.AreEqual(Uri.UriSchemeHttps, uri.Scheme);
        Assert.AreEqual("github.com", uri.Host);
        Assert.AreEqual(
            "https://github.com/wag-k/codex-usage-notifier/blob/main/docs/gmail-oauth-setup.md",
            uri.AbsoluteUri);
    }

    /// <summary>Gmail認証状態の内部enum名を画面表示へ直接露出しないことを検証します。</summary>
    [TestMethod]
    public async Task GmailAuthenticationStates_AreDisplayedInJapanese()
    {
        foreach (GmailAuthenticationState state in Enum.GetValues<GmailAuthenticationState>())
        {
            StubGmailAuthenticationService authentication = new()
            {
                Status = new GmailAuthenticationStatus
                {
                    State = state,
                    HasClientConfiguration = state != GmailAuthenticationState.NotConfigured,
                },
            };
            StatusViewModel viewModel = new(authentication);

            await viewModel.RefreshGmailAuthenticationStatusAsync(CancellationToken.None);

            Assert.AreNotEqual(state.ToString(), viewModel.GmailAuthenticationStatus);
        }
    }

    /// <summary>アプリが公開する全XAMLファイルを連結して読み込みます。</summary>
    /// <returns>公開画面を構成するXAMLの全文です。</returns>
    private static string ReadPublicXaml()
    {
        string root = FindRepositoryRoot();
        string sourceDirectory = Path.Combine(root, "src", "CodexUsageNotifier");
        IEnumerable<string> files = Directory.EnumerateFiles(sourceDirectory, "*.xaml", SearchOption.AllDirectories);
        return string.Join(Environment.NewLine, files.Select(File.ReadAllText));
    }

    /// <summary>テスト実行位置からソリューションを含むリポジトリルートを検索します。</summary>
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

        throw new DirectoryNotFoundException("CodexUsageNotifier.slnを含むリポジトリルートが見つかりません。");
    }
}
