namespace CodexUsageNotifier.Presentation;

/// <summary>
/// Gmail通知の任意性、設定手順、およびプライバシーに関する公開画面用の説明を提供します。
/// </summary>
public static class GmailOnboardingContent
{
    /// <summary>Gmail通知が任意でWindows通知だけでも利用できることを示す説明です。</summary>
    public const string OptionalDescription =
        "Gmail通知は任意の機能です。Googleアカウントを設定しなくても、Windows通知だけでCodex Usage Notifierを利用できます。";

    /// <summary>利用者自身のGoogle CloudプロジェクトでOAuthクライアントを用意する必要性を示す説明です。</summary>
    public const string OAuthClientRequirementDescription =
        "Gmail通知を利用するには、ご自身のGoogle Cloudプロジェクトでデスクトップアプリ用OAuthクライアントを作成する必要があります。現在の公開版では、Codex Usage Notifier共通のOAuthクライアントは提供していません。";

    /// <summary>アプリ内で確認できるOAuth設定の概要手順です。</summary>
    public const string SetupSteps =
        "1. Google Cloudでプロジェクトを作成\n"
        + "2. Gmail APIを有効化\n"
        + "3. デスクトップアプリ用OAuthクライアントを作成\n"
        + "4. OAuthクライアントJSONをダウンロード\n"
        + "5. この画面からJSONを登録\n"
        + "6. Googleアカウントで認証\n"
        + "7. テストメールを送信\n"
        + "8. Gmail通知を有効化";

    /// <summary>現在の実装に対応するGoogle認証とプライバシーの説明です。</summary>
    public const string PrivacyDescription =
        "・Gmailのパスワードは取得しません\n"
        + "・Google認証は既定のWebブラウザーで行います\n"
        + "・認証情報はWindowsユーザー単位で暗号化してPC内へ保存します\n"
        + "・認証情報をCodex Usage Notifier開発者のサーバーへ送信しません\n"
        + "・Gmailの受信メールは読み取りません\n"
        + "・Gmailについては通知メールの送信に必要な権限だけを使用します";
}
