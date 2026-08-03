# Codex Usage Notifier

Codexの利用枠をWindows上で監視し、5時間枠が十分に回復したときに、WindowsとGmailへ通知する常駐アプリです。

Codexを使える状態になったことを見逃さず、必要な開発作業を適切なタイミングで開始できるようにすることを目的とします。

> [!NOTE]
> 初版は「利用枠の監視と通知」に限定します。  
> バックログの自動実行や、作業完了後のバックログ更新は将来機能として設計上の拡張余地だけを確保します。

## 主な機能

- タスクトレイに常駐
- Codex App Serverから利用枠を取得
- 5時間枠と週間枠の残量を表示
- 5時間枠の残量が設定した閾値以上になったときに通知
- Windows通知とGmail通知に対応
- PC起動時・スリープ復帰時にも利用枠を確認
- 同じ回復期間についての重複通知を防止
- 深夜の通知を保留し、通知可能時刻に繰り越し
- 利用履歴を90日間保存
- 一時的な通信失敗から自動復旧
- Windowsログイン時の自動起動に対応

## 初版の通知条件

初期設定では、次の条件を満たしたときに通知します。

1. 5時間枠の残量が99%以上
2. 同じ回復期間について未通知
3. 通知禁止時間外であること

週間枠の残量が少ない場合でも通知は行います。ただし、通知本文に警告を表示します。

通知禁止時間の初期値は `00:00～07:00` です。禁止時間中に通知条件を満たした場合は、07:00以降に保留通知を送信します。

## 通知例

```text
Codexの5時間枠が回復しました

5時間枠：残り100%
週間枠：残り18%
次回リセット：2026/08/04 12:30
確認日時：2026/08/04 07:01

週間枠が少なくなっています。
大規模な作業を開始する前に残量を確認してください。
```

## 想定環境

- Windows 11
- .NET 8
- WPF
- Codex CLIがインストール済み
- Codex CLIでChatGPTアカウントにログイン済み
- Gmail通知を使う場合はGoogleアカウントとGoogle Cloudプロジェクトが必要

## 現在の実装状況

Phase 1の基盤に加え、Phase 2のうちCodex App Serverから利用枠を取得して状態画面へ表示する範囲まで実装しています。

- `codex app-server`を本アプリ所有の子プロセスとして起動
- stdin/stdoutのJSONL形式で`initialize`、`initialized`、`account/rateLimits/read`を実行
- `rateLimitsByLimitId["codex"]`を優先し、なければ`rateLimits`へフォールバック
- 300分を5時間枠候補、10080分を週間枠候補として位置に依存せず識別
- 未識別の枠を破棄せず、画面と機密情報を除いた診断ログへ表示
- 同時取得要求の集約、更新通知後のデバウンス再取得、段階的な再接続

通知判定、Windows通知、Gmail通知、履歴グラフは未実装です。

Codex App Serverの生成済みJSON Schemaは[`docs/codex-app-server-schema`](./docs/codex-app-server-schema)に保存しています。

## 技術構成

```text
CodexUsageNotifier.sln
├─ src/
│  └─ CodexUsageNotifier/
│     ├─ Application/
│     │  ├─ Monitoring/
│     │  ├─ Notifications/
│     │  └─ Settings/
│     ├─ Domain/
│     │  ├─ Models/
│     │  └─ Services/
│     ├─ Infrastructure/
│     │  ├─ Codex/
│     │  ├─ Gmail/
│     │  ├─ Persistence/
│     │  ├─ Startup/
│     │  └─ WindowsNotifications/
│     ├─ Presentation/
│     │  ├─ Tray/
│     │  ├─ Views/
│     │  └─ ViewModels/
│     ├─ App.xaml
│     └─ appsettings.default.json
├─ tests/
│  └─ CodexUsageNotifier.Tests/
├─ docs/
├─ README.md
└─ SPEC.md
```

フォルダ構成は初期案です。責務の分離と単体テストのしやすさを維持できる範囲で、実装時に調整できます。

## 監視の流れ

```text
アプリ起動
   │
   ├─ Codex App Serverを起動・初期化
   │
   ├─ 現在の利用枠を取得
   │
   ├─ 次回リセット時刻にタイマーを設定
   │
   └─ 1時間ごとの補助確認を開始
          │
          ▼
利用枠を再取得
   │
   ├─ 5時間枠が通知閾値以上か
   ├─ 同じ回復期間で未通知か
   ├─ 通知禁止時間外か
   └─ 条件成立
          │
          ├─ Windows通知
          ├─ Gmail通知
          └─ 通知済み状態を保存
```

## 状態表示

初版の画面には、少なくとも次の情報を表示します。

- 5時間枠の残量
- 週間枠の残量
- 次回リセット時刻
- リセット回数
- 最終取得時刻
- 監視状態
- Gmail認証状態
- 最後の通知結果

将来は利用履歴グラフを追加する予定です。

## データ保存先

設定、状態、履歴、ログは次の場所に保存します。

```text
%LOCALAPPDATA%\CodexUsageNotifier\
├─ settings.json
├─ state.json
├─ usage-history.jsonl
├─ auth\
└─ logs\
```

Gmailの認証情報は平文で保存せず、Windowsのユーザー単位の暗号化機能を利用して保護します。

## Gmail通知

Gmail APIとOAuth 2.0を使用します。

- Gmailの通常パスワードは保存しません
- 初回のみブラウザでGoogleアカウントの認証と権限付与を行います
- 送信元と送信先には同じGmailアドレスを指定できます
- 設定画面からテストメールを送信できます
- Gmail通知は個別に無効化できます

自分宛てメールが端末で期待どおり通知されるかは、初期設定時にテストします。

## エラー処理

Codex App Serverとの通信に失敗した場合は、自動的に再接続します。

- 1回目・2回目の失敗：ログに記録して再試行
- 3回連続失敗：Windows通知を表示
- 復旧時：失敗回数をリセット
- Gmail送信失敗：Windows通知とログに記録
- アプリ全体は可能な限り継続動作

## 初版に含めない機能

- バックログの自動取得
- Codexへの作業の自動投入
- Codex作業の完了判定
- GitHub Issueや`BACKLOG.md`の自動更新
- 複数PC間の設定同期
- 利用履歴グラフ
- Windowsサービス化

## 将来構想

将来は、利用枠の回復を契機に、バックログから安全に作業を選び、Codexへ自動投入する仕組みを検討します。

```text
利用枠が回復
   │
   ▼
実行可能なバックログを抽出
   │
   ▼
安全条件と依存関係を確認
   │
   ▼
Codexへ作業を投入
   │
   ▼
ビルド・テスト・差分を検証
   │
   ├─ 成功：バックログを完了へ更新
   └─ 失敗：作業を停止し、人間へ通知
```

この機能では、誤実行や無制限な変更を防ぐため、対象リポジトリ、許可コマンド、変更可能範囲、テスト条件、最大使用量などを別途仕様化します。

## 開発方針

- 初版は段階的に実装する
- 各段階でビルドとテストが通る状態を維持する
- UIと外部サービスをインターフェースで分離する
- 認証情報や個人情報をGitへコミットしない
- 各クラス、メソッド、プロパティに日本語コメントを付ける
- 通知判定、重複防止、禁止時間、再試行処理には単体テストを付ける

## 推奨実装順

1. WPF・タスクトレイ・DI・ログ・テストのひな型
2. Codex App Serverの起動とJSON-RPC通信
3. 利用枠の取得と画面表示
4. 通知判定と重複防止
5. Windows通知
6. スリープ復帰と自動再接続
7. Gmail API・OAuth認証・テスト送信
8. 自動起動と初回設定
9. 履歴保存と保守処理
10. 配布用ビルド

## 公式資料

- OpenAI Codex App Server  
  https://developers.openai.com/codex/app-server
- Gmail API: Create and send email messages  
  https://developers.google.com/workspace/gmail/api/guides/sending
- Google OAuth 2.0 for installed applications  
  https://developers.google.com/identity/protocols/oauth2/native-app
- Google API Client Library for .NET: OAuth 2.0  
  https://developers.google.com/api-client-library/dotnet/guide/aaa_oauth

詳細な要件と受け入れ条件は [SPEC.md](./SPEC.md) を参照してください。
