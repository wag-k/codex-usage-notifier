# Gmail通知のためのGoogle OAuth設定

Codex Usage NotifierのGmail通知は任意です。Googleアカウントを設定しなくても、Windows通知だけで利用できます。

現在の公開版では、利用者自身のGoogle Cloudプロジェクトにデスクトップアプリ用OAuthクライアントを作成する方式です。Codex Usage Notifier共通のOAuthクライアントは提供していません。

> [!IMPORTANT]
> OAuthクライアントJSONや認証情報を、リポジトリ、Issue、ログ、チャットへ貼り付けないでください。

## 1. Google Cloudプロジェクトを用意する

1. [Google Cloud Console](https://console.cloud.google.com/)へログインします。
2. 既存のプロジェクトを選ぶか、新しいプロジェクトを作成します。
3. 以降の操作で同じプロジェクトが選択されていることを確認します。

## 2. Gmail APIを有効化する

1. Google Cloud Consoleの「APIとサービス」または「APIライブラリ」を開きます。
2. `Gmail API`を検索します。
3. 対象プロジェクトで「有効にする」を選びます。

公式資料: [Gmail APIを有効化して認証情報を作成する](https://developers.google.com/workspace/gmail/api/quickstart/dotnet)

## 3. Google Auth Platformを設定する

Google Cloud Consoleの「Google Auth Platform」で、画面に従って次を設定します。Consoleの表示名はGoogle側の更新で変わることがあります。

1. 「Branding」でアプリ名、ユーザーサポートメール、デベロッパー連絡先を設定します。
2. 「Audience」で利用者の種類を選びます。
   - 個人のGoogleアカウントなど組織外アカウントを使う場合は、通常「External」です。
   - Google Workspace組織内だけで使う場合は、組織のポリシーに従って「Internal」を選べる場合があります。
3. 公開ステータスが「Testing」の場合は、認証に使うGoogleアカウントをテストユーザーへ追加します。
4. 「Data Access」で、後述のスコープを確認します。

「Testing」ではテストユーザー数に上限があります。また、`openid`、`email`等だけでなく`gmail.send`も要求するため、認証とリフレッシュトークンが7日で期限切れになる場合があります。Public Betaの検証中に再認証が必要になる場合は、この公開ステータスも確認してください。

公式資料:

- [Google Auth PlatformのAudience設定](https://support.google.com/cloud/answer/15549945)
- [デスクトップアプリ向けOAuth 2.0](https://developers.google.com/identity/protocols/oauth2/native-app)

## 4. デスクトップアプリ用OAuthクライアントを作成する

1. Google Auth Platformの「Clients」または「認証情報」を開きます。
2. OAuthクライアントを新規作成します。
3. アプリケーションの種類に「Desktop app（デスクトップアプリ）」を選びます。
4. 判別しやすい名前を付けて作成します。
5. 作成したクライアントのJSONをダウンロードします。

Webアプリ用クライアントは使用できません。Codex Usage Notifierは、システム既定ブラウザー、PKCE、ローカルループバックリダイレクトを使うデスクトップアプリ向けOAuthフローを実行します。

## 5. Codex Usage NotifierへJSONを登録する

1. タスクトレイの「設定」、または状態画面の「設定を開く」を選びます。
2. 「Gmail通知とGoogle認証」で「OAuthクライアントJSONを選択」を選びます。
3. ダウンロードしたJSONを選択します。
4. 「OAuthクライアント：設定済み」と表示されることを確認します。

検証済みのJSONは次へコピーされます。

```text
%LOCALAPPDATA%\CodexUsageNotifier\auth\google-oauth-client.json
```

JSON自体はDPAPI暗号化対象ではありませんが、LocalAppData内へ保存され、Git管理対象にはしません。不正なJSONを選んだ場合、既存の有効なファイルは上書きされません。

## 6. Googleアカウントで認証する

1. 「Googleアカウントで認証」を選びます。
2. システム既定ブラウザーで、使用するGoogleアカウントを選びます。
3. 表示された権限を確認して同意します。
4. 設定画面に「認証済み」とアカウントが表示されることを確認します。

要求するスコープは次の3つです。

| スコープ | 用途 |
|---|---|
| `https://www.googleapis.com/auth/gmail.send` | Gmail APIで通知メールを送信する |
| `openid` | 認証したGoogleアカウントを識別する |
| `email` | 認証済みメールアドレスを画面と送信元に使用する |

`gmail.send`はGoogleの分類ではSensitive scopeです。Codex Usage Notifierは、Gmailの受信メール、メール一覧、本文、削除、設定変更の権限を要求しません。

公式資料: [Gmail APIのOAuthスコープ](https://developers.google.com/workspace/gmail/api/auth/scopes)

## 7. テストメールを送信してGmail通知を有効化する

1. 送信先メールアドレスを確認します。認証時に空欄なら、認証したアドレスが入力されます。
2. 「テストメールを送信」を選びます。
3. PC、スマートフォン、またはタブレットでメール到着を確認します。
4. 必要に応じて「Gmail通知を有効にする」をオンにし、「保存」を選びます。

メール送信にはSMTPではなくGmail APIの`users.messages.send`を使用します。テストメールは本番通知の送信済み状態、回復連番、利用枠履歴を変更しません。

## 8. 認証情報とプライバシー

- Gmailの通常パスワードは取得しません。
- 認証情報はWindows DPAPIのCurrentUserで暗号化し、同じWindowsユーザーだけが復号できる形式でPC内へ保存します。
- 保存先は`%LOCALAPPDATA%\CodexUsageNotifier\auth\`です。
- OAuth認証情報をCodex Usage Notifier開発者のサーバーへ送信しません。
- Gmailの受信メールは読み取りません。
- OAuthトークンや`client_secret`をアプリログへ出力しません。

## 9. 認証解除と再認証

### 認証解除

設定画面の「認証解除」を選び、確認ダイアログで実行します。Google側のトークン失効を可能な範囲で試みた後、ローカルの暗号化済み認証情報を削除し、Gmail通知を無効にします。Google側の失効要求が失敗した場合でも、選択に従ってローカル情報を削除できます。

### 再認証

「再認証が必要」と表示された場合は「再認証」を選びます。再認証後は新しい配送有効期間として扱うため、認証失効中に成立した古い通知をまとめて送りません。

Googleアカウント側から権限を取り消す場合は、Googleアカウントの「サードパーティ製のアプリとサービス」も確認してください。

## 10. よくあるエラー

### OAuthクライアントが未設定

デスクトップアプリ用OAuthクライアントJSONを作成し、設定画面から登録してください。Gmailが未設定でもWindows通知は利用できます。

### JSONが拒否される

Webアプリ用ではなくデスクトップアプリ用であること、JSONが途中で変更・破損していないことを確認してください。不正なファイルで既存設定は上書きされません。

### ブラウザー認証が完了しない

ブラウザーを閉じた、認証をキャンセルした、またはローカルループバック通信がファイアウォール等で遮断された可能性があります。アプリへ戻り、再度認証してください。

### 401 / `invalid_grant`

Google側で権限が取り消された、リフレッシュトークンが失効した、またはTesting状態の期限に達した可能性があります。設定画面から再認証してください。

### 403 `insufficientPermissions`

必要な`gmail.send`権限が許可されていません。Google Auth PlatformのData Accessと同意内容を確認し、再認証してください。

### 403 `accessNotConfigured` / `serviceDisabled`

OAuthクライアントを作成したGoogle CloudプロジェクトでGmail APIが有効か確認してください。このエラーは再認証だけでは解消しません。

### その他の403

Google Workspace管理者のポリシーやアカウント制限など、恒久的な拒否の可能性があります。Google Cloud Console、Google Workspaceのポリシー、認証アカウントを確認してください。

### 一時的な通信エラー / 429 / 5xx

本番通知は初回失敗から60分後以降の次回正常監視で1回だけ再試行します。専用の短時間再試行タイマーは使用しません。テストメールは画面から再実行してください。

## 公式資料

- [OAuth 2.0 for Desktop Apps](https://developers.google.com/identity/protocols/oauth2/native-app)
- [OAuth 2.0 Policies](https://developers.google.com/identity/protocols/oauth2/policies)
- [OAuthのセキュリティベストプラクティス](https://developers.google.com/identity/protocols/oauth2/resources/best-practices)
- [Gmail API scopes](https://developers.google.com/workspace/gmail/api/auth/scopes)
- [Gmail APIでメールを送信する](https://developers.google.com/workspace/gmail/api/guides/sending)
