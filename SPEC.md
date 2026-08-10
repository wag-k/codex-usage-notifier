# Codex Usage Notifier 仕様書

## 1. 文書情報

| 項目 | 内容 |
|---|---|
| 文書名 | Codex Usage Notifier 仕様書 |
| 対象バージョン | 初版（MVP） |
| 作成日 | 2026-08-04 |
| 最終更新日 | 2026-08-11（Phase 5A） |
| 対象OS | Windows 11 |
| 開発基盤 | .NET 8 / WPF |
| 主目的 | 任意のCodex利用枠を監視し、期間に応じた回復・リセット前・リセット完了をWindowsとGmailへ通知する |

## 2. 背景

Codexの利用枠が回復していても、または長期枠のリセットが近づいていても、ユーザーがその状態に気付かなければ、利用可能な時間を有効に活用できない。

本アプリは、Codex App Serverが返す任意の利用枠をWindows PC上で観測し、短期枠の回復、長期枠のリセット前、および長期枠の新しい利用期間の開始を通知する。初版では、人間が通知を確認してCodexの作業を開始または調整する。

2026年8月4日にCodex CLI `0.145.0-alpha.18`で実アカウントを確認した結果、`limitId=codex`のprimaryに10080分・使用率35%の週間枠だけが存在し、secondaryはnull、`rateLimitResetCredits.availableCount`は1、300分枠は未観測だった。このため、5時間枠の存在を前提にせず、利用枠の期間に応じて通知目的を分ける。`availableCount`は利用可能なrate-limit reset credit数であり、通常の周期的な利用枠リセット回数ではない。

未使用分が次の利用期間へ繰り越されることは確認できていない。本アプリでは、リセット前に残量を確認できるよう通知するが、未使用分が必ず繰り越される、または必ず消滅するとは断定しない。

将来は、承認済みのバックログから作業を自動選択し、Codexへ投入し、作業結果に応じてバックログを更新する仕組みへ拡張する。

## 3. 用語

| 用語 | 定義 |
|---|---|
| 5時間枠候補 | `windowDurationMins == 300`の利用枠。存在は保証されない |
| 週間枠候補 | `windowDurationMins == 10080`の利用枠。存在は保証されない |
| 短期枠 | FiveHourなど、回復を通知する目的で扱う利用枠 |
| 長期枠 | Weeklyなど、リセット前とリセット完了を通知する目的で扱う利用枠 |
| Position | App Server内の格納位置。PrimaryまたはSecondaryであり、枠の意味を表さない |
| Classification | ウィンドウ長によるFiveHour、Weekly、Unknownの分類 |
| 利用枠別通知設定 | LimitId、Position、WindowDurationMinutesで識別した利用枠ごとの有効な通知種類 |
| 残量 | `100 - usedPercent` で算出する利用可能割合 |
| リセット期間 | 同一の`resetsAt`または同一ウィンドウとして識別する利用期間 |
| リセット期間ID | 利用枠ごとのリセット期間を重複通知防止に使用できる識別子 |
| 通知種別 | ShortWindowRecovered、LongWindowEarlyWarningなど通知目的を表す値 |
| 通知段階 | 同じ通知種別内の段階。長期枠ではEarly、Standard、Finalなどを使用する |
| 通知禁止時間 | 即時通知を行わず、通知を保留する時間帯 |
| 保留通知 | 通知禁止時間中に成立した候補を保存し、禁止時間終了後の再取得・再判定を経て送る通知 |
| バックログ | 将来実施する作業を優先順位付きで管理する一覧 |

## 4. システム概要

### 4.1 構成

```text
┌───────────────────────────┐
│ Codex Usage Notifier      │
│                           │
│  ┌─────────────────────┐  │
│  │ Monitoring Service  │  │
│  └─────────┬───────────┘  │
│            │ JSON-RPC      │
│  ┌─────────▼───────────┐  │
│  │ Codex App Server    │  │
│  └─────────────────────┘  │
│                           │
│  ┌─────────────────────┐  │
│  │ Notification Policy │  │
│  └───────┬───────┬─────┘  │
│          │       │         │
│  ┌───────▼───┐ ┌─▼──────┐ │
│  │ Windows   │ │ Gmail  │ │
│  │ Notifier  │ │ Sender │ │
│  └───────────┘ └────────┘ │
│                           │
│  ┌─────────────────────┐  │
│  │ Local Persistence   │  │
│  └─────────────────────┘  │
└───────────────────────────┘
```

Notification Policyは、Positionではなく利用枠別通知設定、Classification、期間を基準に、取得できたすべての枠について短期枠回復、長期枠リセット前、長期枠リセット完了を独立に判定する。

Phase 5Aの起動順序は次のとおりとする。運用保守はUsageMonitor開始後に非同期で開始し、完了待ちで監視を遅らせない。

```text
アプリ起動
  ↓
単一インスタンス取得
  ↓
保存ディレクトリ準備・設定読み込み
  ↓
state schema検証と段階migration
  ↓
AutoStart設定同期（失敗は非致命）
  ↓
UI／Tray初期化
  ↓
UsageMonitor開始
  ↓
運用保守をバックグラウンド開始
```

### 4.2 外部インターフェース

#### Codex App Server

利用枠の取得には、Codex App ServerのJSON-RPCインターフェースを使用する。

- 初期化：`initialize`
- 初期化完了通知：`initialized`
- 利用枠取得：`account/rateLimits/read`
- 利用枠更新通知：`account/rateLimits/updated`

App Serverの仕様変更に備え、JSON-RPC通信とレスポンス解釈は専用クラスへ分離する。

### 4.3 Phase 2で確定したCodex連携仕様

Phase 1完了時の懸念点1「App Serverのプロセス所有権」と懸念点2「複数契機による同時取得」は、次の方針で解消する。

#### JSON-RPC通信

1. `codex app-server --listen stdio://`を子プロセスとして起動する。
2. stdin/stdoutをJSONL専用とし、1行を1つのJSON-RPCメッセージとして扱う。
3. App Serverのワイヤー形式に合わせ、`jsonrpc: "2.0"`フィールドは送信しない。
4. `initialize`の成功応答を受信してから`initialized`通知を送信する。
5. App Server起動タイムアウトと`initialize`タイムアウトは15秒、通常要求タイムアウトは10秒とする。
6. タイムアウトと終了処理には`CancellationToken`を使用する。
7. 要求IDごとに`TaskCompletionSource`を保持し、対応する応答だけで要求を完了する。
8. プロセス終了時は待機中の全要求を失敗させる。
9. 不正なJSONや未知の通知を受信してもアプリ全体を終了しない。
10. stderrは機密情報の可能性がある行をマスクし、診断ログへ転送する。

#### プロセス所有権

1. 本アプリが起動したApp Serverだけを管理し、プロセスIDを保持する。
2. 既存のCodexプロセスを検索、再利用、終了しない。
3. アプリ終了時は所有プロセスのstdinを閉じて正常終了を要求する。
4. 5秒以内に終了しない場合は、所有するプロセスツリーだけを強制終了する。
5. App Serverが予期せず終了した場合は、FR-014の1分、5分、15分の再試行方針を使用する。
6. 既定ではPATH上の`codex`コマンドを使用し、WindowsではPATHEXTに従って`.exe`、`.cmd`、`.bat`などの実体を解決する。
7. PATHで解決できない環境向けに、実行コマンドまたは実行ファイルのパスを設定で変更できるようにする。
8. WindowsApps配下の実体ファイルをコピーして起動する機能は実装しない。

#### 利用枠の識別

1. `rateLimitsByLimitId`が存在する場合は、すべてのlimitIdと、そのprimary・secondaryを保持する。
2. 現在形式が存在しない場合だけ後方互換用の`rateLimits`を使用する。
3. `primary`と`secondary`は位置情報として保持し、枠の種類を決定しない。
4. `windowDurationMins == 300`を5時間枠候補とする。
5. `windowDurationMins == 10080`を週間枠候補とする。
6. それ以外の長さはUnknownとして保持する。同じ既知長の重複や異なるlimitIdも破棄しない。
7. `account/rateLimits/updated`の通知内容だけでは状態を確定せず、1秒のデバウンス後に`account/rateLimits/read`を再実行する。
8. 診断ログには`limitId`、`primary`／`secondary`の由来、`windowDurationMins`、使用率、リセット時刻、識別結果だけを出力し、認証情報や生JSONは出力しない。
9. 実アカウントでは`limitId=codex`、primaryが10080分、secondaryがnull、300分枠なしを観測済みであり、この構成を単体テストへ固定する。
10. 5時間枠候補が存在しない状態を正常な取得結果として扱う。
11. FiveHourは短期枠の回復通知、Weeklyは長期枠のリセット前・リセット完了通知に使用する。
12. Unknownは表示と履歴保存の対象とするが、初期設定では通知対象にしない。

#### 利用枠別通知設定

1. 利用枠はLimitId、Position、WindowDurationMinutesの3項目で識別する。
2. 複数の利用枠を同時に通知対象として有効化できる。
3. FiveHourの既定値は短期枠回復通知だけを有効とする。
4. Weeklyの既定値はEarly、Standard、Final、リセット完了通知を有効とする。
5. Unknownの既定値はすべての通知を無効とするが、表示、履歴保存、新規検出ログの対象には残す。
6. 保存済みの利用枠別設定がある場合は、Classificationによる既定値より完全一致した設定を優先する。
7. 通知済み状態は利用枠ごとの複合キーで独立して保持し、別の利用枠の通知を抑止しない。

#### 同時要求の集約

1. 利用枠取得は常に最大1件だけ実行する。
2. 取得中の追加要求は、取得契機の数にかかわらず再取得要求1件へ集約する。
3. 現在の取得終了後、再取得要求があればもう1回取得する。
4. タイマー、スリープ復帰、手動更新、更新通知が重なっても要求を無制限に積まない。

### 4.4 Gmail API

Phase 4Bの認証とテストメール、およびPhase 4Cの本番通知にはGmail APIを使用する。

- 認証方式：OAuth 2.0
- アプリ種別：デスクトップアプリ
- 認証UI：システム既定ブラウザー
- リダイレクト：ローカルループバックIP
- 認証コード保護：PKCE
- メール送信：`users.messages.send`
- `userId`：認証ユーザー自身を示す`me`
- メール形式：MIME形式をBase64URLエンコード
- 送信元：認証したGoogleアカウント
- 送信先：初期値は送信元と同じアドレス
- OAuthスコープ：`https://www.googleapis.com/auth/gmail.send`、`openid`、`email`

`gmail.send`はメール送信、`openid`と`email`は認証アカウントの識別とメールアドレス表示にだけ使用する。Gmail本文・一覧の読み取り、削除、設定変更、Google Drive、Google Contactsの権限は要求しない。

Phase 4BはGoogle認証と設定画面からのテストメール送信を提供する。Phase 4C-1は共通通知候補のGmail本番配送、同一取得候補の1通集約、チャネル別状態保存、Phase 4C導入前の通知を除外する開始境界、および共通通知禁止時間を実装する。Phase 4C-2は一時障害の60分後1回再試行、認証異常、通知禁止時間、再起動時の送信中状態復旧、および配送有効期間境界を実装する。

## 5. 対象範囲

### 5.1 初版に含むもの

- タスクトレイ常駐
- App Serverが返すすべてのlimitIdと利用枠の取得
- 現在残量と次回リセット時刻の表示
- Windows通知
- Gmail通知
- 短期枠の回復通知
- 長期枠の段階的なリセット前通知
- 長期枠の再取得確認後のリセット完了通知
- 通知種別ごとの閾値と有効・無効の設定
- 重複通知防止
- 通知禁止時間と保留通知
- PC起動時の確認
- スリープ復帰時の確認
- 次回リセット時刻を利用した監視
- 1時間ごとの補助確認
- 自動再接続
- 3回連続失敗時の通知
- Windowsログイン時の自動起動
- 利用履歴の90日保存
- ログ保存
- 初回設定画面
- Gmailテスト送信

### 5.2 初版に含まないもの

- バックログの自動取得
- Codex作業の自動実行
- 自動コミット・自動プッシュ
- GitHub Issueの自動更新
- `BACKLOG.md`の自動更新
- 利用履歴グラフ
- 複数PC間同期
- Windowsサービス
- Web管理画面
- スマートフォン専用アプリ

## 6. 機能要件

### FR-001 タスクトレイ常駐

1. アプリは起動後、タスクトレイに常駐する。
2. メインウィンドウを閉じてもアプリを終了しない。
3. タスクトレイメニューに次を表示する。
   - 状態を開く
   - 今すぐ確認
   - 設定
   - Windowsテスト通知
   - ログフォルダを開く
   - 終了
4. 明示的に「終了」を選択した場合のみプロセスを終了する。
5. DI、永続化、App Server、監視、トレイの初期化前に、Windowsユーザー単位の`Local`名前付きMutexを取得する。
6. 同じWindowsユーザーで既存インスタンスを検出した場合は、案内を表示して新しいインスタンスを終了する。
7. 2個目のインスタンスはApp Server、監視、Gmail、Windows通知、トレイを開始せず、`state.json`と履歴を変更しない。
8. 所有インスタンスの正常終了・異常終了後は、MutexのOSハンドル解放により次の起動を許可する。

### FR-002 初回起動

初回起動時に、次を設定する。

1. Windowsログイン時に自動起動するか
   - 初期表示：有効
   - ユーザーが変更可能
2. Gmail通知を有効にするか
3. Gmailアカウントの認証
4. 送信先メールアドレス
   - 初期値：認証アカウントと同じアドレス
5. テストメール送信
6. 短期枠回復通知
   - 初期表示：有効
   - 残量閾値の初期値：99%
7. 通知禁止時間
   - 初期値：00:00～07:00
8. 長期枠リセット前通知
   - 初期表示：有効
   - 早期通知：48時間以内かつ残量50%以上
   - 通常通知：24時間以内かつ残量20%以上
   - 最終通知：6時間以内かつ残量10%以上
9. 長期枠リセット完了通知
   - 初期表示：有効
10. 利用枠別の通知種類
   - FiveHour：短期枠回復通知だけを有効
   - Weekly：Early、Standard、Final、リセット完了通知を有効
   - Unknown：すべて無効

初回設定を完了しなくても、Windows通知だけで監視を開始できる。

### FR-003 Codex App Server起動

1. アプリはCodex App Serverを子プロセスとして起動できる。
2. 標準入力と標準出力を使用してJSON-RPC通信を行う。
3. 起動後、`initialize` と `initialized` を正しい順序で送る。
4. プロセス終了を検知した場合は再起動を試みる。
5. アプリ終了時は、アプリが起動したApp Serverプロセスを終了する。
6. Codex CLI未インストール、未ログイン、実行ファイル未検出を区別して表示する。

### FR-004 利用枠取得

1. `account/rateLimits/read` を使用して利用枠を取得する。
2. `rateLimitsByLimitId`が返すすべてのlimitIdとprimary・secondary位置を保持する。
3. 各枠について次を取得または算出する。
   - LimitId
   - LimitName
   - Position
   - Classification
   - 使用率
   - 残量
   - ウィンドウ長
   - 次回リセット時刻
   - PlanType
   - RateLimitReachedType
4. 取得可能な場合は`rateLimitResetCredits.availableCount`を「利用可能リセットクレジット数」として表示する。通常の周期的なリセット回数とは解釈しない。
5. 未知の枠が追加されても、アプリが異常終了しない。
6. 取得結果はUTCで内部保持し、画面と通知ではローカル時刻に変換する。
7. 300分の利用枠が存在しなくても取得成功として扱う。

### FR-005 監視スケジュール

1. アプリ起動時に即時取得する。
2. 「今すぐ確認」の操作時に即時取得する。
3. PCのスリープ復帰時に即時取得する。
4. 次回リセット時刻の直後に取得する。
   - 初期値：`resetsAt` の60秒後
5. 取得漏れ防止のため、1時間ごとに補助確認する。
6. `account/rateLimits/updated` を受信した場合は、必要に応じて表示と通知判定を更新する。
7. 同時に複数の取得処理を実行しない。
8. 取得中の再要求は、1回の再取得要求へ集約する。

### FR-006 短期枠の回復通知

利用枠別設定で短期回復通知を有効にした枠について、以下をすべて満たす場合に`ShortWindowRecovered`通知候補とする。

1. 短期枠回復通知が有効である。
2. 残量が短期枠回復通知の閾値以上である。
3. 同じ利用枠・リセット期間・通知種別について未通知である。
4. 取得結果が正常である。

閾値は1～100%の範囲で設定でき、初期値は99%とする。`resetsAt`がない場合は、利用枠ごとに永続化した回復連番を持ち、残量が一度閾値未満になった後で閾値以上へ遷移した場合だけ連番を増やす。過去状態がなく起動時点で閾値以上の場合は回復連番1の初回回復として最大1回通知できる。期間IDは`no-reset-time:{limitId}:{position}:{windowDurationMinutes}:recovery-sequence-{n}`形式とする。300分枠が存在しない状態も正常とする。

### FR-007 リセット期間の識別と重複通知防止

重複通知防止のため、利用枠ごとのリセット期間を一意に識別する。

期間識別は通知種類に応じて次を使用する。

1. `resetsAt`がある場合は、App Serverから取得したリセット時刻を使用する。
2. `resetsAt`がない短期回復通知では、永続化した回復連番を使用する。
3. `resetsAt`がない長期枠のリセット完了推定では、使用率低下を検出した取得イベントを識別する値を使用する。

通知済み状態は、次の組み合わせで識別する。

- LimitId
- Position
- WindowDurationMinutes
- リセット期間ID
- 通知種別
- 通知段階

通知種別には少なくとも`ShortWindowRecovered`、`LongWindowEarlyWarning`、`LongWindowStandardWarning`、`LongWindowFinalWarning`、`LongWindowResetCompleted`、`NewRateLimitDetected`、`MonitoringFailure`を表現できるようにする。同じリセット期間において、同じ通知種別と通知段階をWindowsとGmailへそれぞれ最大1回送る。

送信失敗した通知先は、チャネルごとの`AttemptCount`、`LastAttemptedAtUtc`、`NextRetryAtUtc`を保持する。Windows通知は5分間隔・最大3回で再送し、送信前に保存した`InProgress`が5分以上残っている場合は中断された試行として次の正常取得時に再試行可能な状態へ戻す。Gmailは一時障害だけを初回失敗から60分後以降の次回正常取得で1回再試行し、初回と合わせて最大2回とする。Gmailの`InProgress`が60分以上残った場合も試行回数を巻き戻さず、最大2回の範囲で回復する。WindowsとGmailは候補を共有するが配送状態を独立して評価し、一方が未送信でも成功済みの他方へ再送しない。

### FR-008 PC起動時の通知

1. 起動時点で短期枠回復または長期枠リセット前の条件を満たす場合も通知候補とする。
2. 保存済み状態から、同じリセット期間・通知種別・通知段階ですでに通知済みと判定できる場合は通知しない。
3. 保存データが破損または存在しない場合は、安全側として現在状態を表示する。
4. 初回起動直後の通知は、初回設定完了後に行う。

### FR-009 通知禁止時間

1. 通知禁止時間を開始時刻と終了時刻で設定できる。
2. 初期値は00:00～07:00とする。
3. 日付をまたぐ時間帯に対応する。
4. 禁止時間中に通知条件を満たした場合、通知を保留する。
5. 禁止時間終了時に利用枠を再取得する。
6. 短期枠回復通知は、保存した条件成立時刻と再取得した現在値を含めて送信できる。
7. 長期枠リセット前通知は、禁止時間終了時点でも対象段階の時間帯と残量条件が有効な場合だけ送る。期限を過ぎた段階は送らない。
8. 長期枠リセット完了通知は、禁止時間終了後に新しいリセット期間を確認できれば送信できる。
9. 保留通知には、実際に条件を満たした時刻、送信時点の残量、および通知段階を記載する。
10. 保留通知は`DeferredUntilUtc`を過ぎ、現在のリセット期間IDと一致し、条件成立から24時間以内の場合だけ復元する。
11. 期間不一致、利用枠消失、または24時間超過を検出した保留は`Expired`へ変更する。`resetsAt`のない使用率低下推定イベントは、同一利用枠かつ24時間以内であることを確認する。
12. Gmailの初回送信と再試行にも同じ通知禁止時間を適用し、禁止時間を理由として`GmailAttemptCount`を増やさない。
13. Gmail再試行期限が禁止時間中に到来した場合は、禁止時間終了後の最初の正常取得で候補の有効性と最大試行回数を再判定する。

### FR-010 Windows通知

Windows通知には少なくとも次を含める。

- タイトル
- 通知種別と通知段階
- 通知条件を満たした利用枠のLimitId、Position、Classification、ウィンドウ長、残量
- 次回リセット時刻
- リセットまでの残り時間
- 確認時刻

通知をクリックした場合は、状態画面を開く。

Windows通知が利用できない場合でも、Gmail通知と監視処理は継続する。

同一取得で複数の通知候補が成立した場合、共有`NotifyIcon`への連続表示で上書きされないよう、候補を1件のWindows通知へ集約する。集約通知の表示要求が成功した場合だけ、含まれる各候補のWindows配送状態を成功へ変更する。個別通知が必要になった場合はWindowsトースト通知への移行を検討する。

タスクトレイの「テスト通知」サブメニューから、短期回復、Early、Standard、Final、リセット完了、監視障害を個別に送信できる。テスト通知は本番の通知済み状態、回復連番、利用枠履歴を変更せず、送信結果だけをログへ記録する。テスト通知のクリック時も状態画面を開く。

### FR-011 Gmail認証

1. Gmail APIのOAuth 2.0を使用する。
2. 認証にはシステム既定ブラウザを使用する。
3. デスクトップアプリ用OAuthクライアント、PKCE、ローカルループバックIPリダイレクトを使用し、Device Code Flow、埋め込みブラウザー、OOBコピーは使用しない。
4. Gmailの通常パスワードを取得・保存しない。
5. `gmail.send`、`openid`、`email`だけを要求し、用途は4.4に従う。
6. OAuthクライアント設定の標準配置先を`%LOCALAPPDATA%\CodexUsageNotifier\auth\google-oauth-client.json`とする。
7. 設定画面から選択したJSONは、デスクトップアプリ用の必須項目とループバックURIを検証し、正常な場合だけ一時ファイルから標準配置先へ置換する。不正時は既存ファイルを上書きしない。
8. クライアントIDとクライアントシークレットをソースへ埋め込まず、設定ファイルと認証情報ファイルをGit管理対象外とする。
9. 認証処理は最大1件とし、多重ブラウザー起動を防ぐ。アプリ終了とユーザー操作でキャンセルでき、リダイレクト待機は5分でタイムアウトする。
10. `state`検証、PKCE、認証コード交換はGoogle公式クライアントライブラリへ委譲する。
11. 認証状態として`NotConfigured`、`Unauthenticated`、`Authenticating`、`Authenticated`、`RefreshRequired`、`ReauthenticationRequired`、`Error`を表現する。
12. 認証状態には認証済みメールアドレス、最終認証成功UTC時刻、最終トークン更新UTC時刻、安全な最終エラー概要、クライアント設定有無、テスト送信可否、再認証要否を保持できる。トークン本体を画面モデルと`AppSettings`へ含めない。
13. アクセストークン、リフレッシュトークン、IDトークンを含むGoogleデータストア全体を、Windows DPAPIの`CurrentUser`スコープで暗号化する。
14. 暗号化済み認証情報は`%LOCALAPPDATA%\CodexUsageNotifier\auth\google-oauth-credentials.dat`へ、スキーマバージョン付き形式で一時ファイルから置換して保存する。
15. Google公式クライアントの平文ファイルデータストアは使用しない。`IDataStore`をDPAPIストアへ差し替え、更新されたトークンも同じストアへ保存する。
16. ファイル破損、復号失敗、別Windowsユーザーからの読み込みはアプリ全体を終了させず、`ReauthenticationRequired`として案内する。
17. 期限切れアクセストークンはGoogle公式クライアントで単一実行更新する。通常の更新成功ではブラウザー再認証を要求しない。
18. `invalid_grant`、権限取消、リフレッシュトークン失効、Gmail API 401では再認証必要状態へ移行する。一時的なネットワーク障害では認証情報を削除しない。
19. 認証解除前に確認し、Google側のトークン失効を可能な範囲で試みた後、ローカル認証情報を削除して`GmailNotificationEnabled`をfalseへ保存する。送信先設定は維持してよい。
20. Google側の失効失敗とローカル削除結果を区別し、Google側失効が失敗してもユーザーが明示したローカル削除は試行する。ローカル削除失敗を隠さない。
21. OAuth設定なし・不正、ユーザーキャンセル、ブラウザーを閉じた後の待機タイムアウト、ローカルポート使用不可、ネットワーク障害、OAuthサーバーエラー、権限拒否、リフレッシュトークン未取得、更新失敗、`invalid_grant`、権限取消、暗号化・復号・保存・削除失敗を安全な状態とメッセージへ分類する。
22. ユーザーキャンセルは正常操作として`Unauthenticated`を維持する。ブラウザーを閉じてコールバックがない場合はタイムアウト後に再試行を案内する。
23. 認証サービス、資格情報ストア、Google APIクライアント、MIME生成、テスト送信、認証状態表示を分離し、OAuth、暗号化、MIME、API通信をViewModelとコードビハインドへ実装しない。

### FR-012 Gmail送信

1. Gmail通知の有効・無効を設定できる。
2. 送信先の初期値は、認証したGmailアドレスと同じアドレスとする。
3. 送信先は設定画面で変更できる。
4. Phase 4C-1の本番通知件名は、1候補なら通知種別を判別できる内容、複数候補なら件数を示す内容とする。

5. Phase 4C-1の本番通知本文に次を含める。
   - 通知種別と通知段階
   - 通知対象のLimitId、Position、Classification、ウィンドウ長、残量
   - 次回リセット時刻
   - リセットまでの残り時間
   - 条件成立時刻
   - 確認時刻
6. MIMEメールをBase64URL形式でエンコードし、Gmail APIで送信する。
7. Phase 4Bでは設定画面からUTF-8日本語の件名・本文を持つテストメールを個別送信できる。SMTPは使用しない。
8. 自分宛てメールで端末通知が届くか、初期設定時に確認を促す。
9. Phase 4Bのテスト送信結果は画面と安全なログだけへ記録し、本番の`RateLimitNotificationState`、`GmailDeliveryStatus`、試行回数、回復連番、利用枠履歴を作成・変更しない。
10. テスト送信は同時に最大1件とし、認証済みかつ送信先が有効な場合だけ実行できる。
11. Gmail APIの401、権限不足403、API未有効化403、未知の恒久403、一時的なネットワーク・サーバー障害を区別し、安全で操作可能な概要を表示する。401と`insufficientPermissions`等の明確な権限不足403は`Authentication`として`ReauthenticationRequired`へ移行し、`accessNotConfigured`と`serviceDisabled`は再認証を要求しない`Permanent`とする。
12. 通知候補は`RateLimitNotificationPolicy`で一度だけ生成し、WindowsとGmailで共有する。Gmail固有の通知条件判定を複製しない。
13. WindowsとGmailの配送状態は独立させ、成功済みチャネルへ他方の成否を理由として再送しない。
14. 初回Phase 4C起動時に`GmailProductionDeliveryStartedAtUtc`をUTCで永続化し、原則として保存済み状態の`ConditionMetAtUtc`が開始時刻以上の通知だけをGmail本番配送対象とする。
15. Gmail本番配送は、Gmail通知有効、認証情報が利用可能、送信先有効、開始境界以降、Gmail未試行、通知禁止時間外をすべて満たす場合だけ実行する。
16. 同じ利用枠取得で複数のGmail対象候補が成立した場合は1通へ集約する。候補が異なるlimitIdに属する場合も同じ取得単位で集約する。
17. 集約しても`GmailDeliveryStatus`と試行情報は候補ごとに保持し、成功時は全候補を`Succeeded`、失敗時は全候補を`Failed`とする。Windows配送状態は変更しない。
18. 本番通知のMIME生成とBase64URL変換、およびGmail APIクライアントはPhase 4Bの共通サービスを再利用する。テストメール送信と本番通知配送の上位責務は分離する。
19. `UsageDropInference`によるリセット完了は使用率低下からの推定であることを本文へ明記し、`ResetTimeAdvanced`と区別する。
20. 未使用分が次の利用期間へ繰り越されるか、または消滅するかは本文で断定しない。
21. 一時的なネットワーク障害、タイムアウト、Gmail API 429・5xxでは、初回失敗時に`GmailDeliveryStatus=Failed`、`GmailAttemptCount=1`、`GmailLastAttemptedAtUtc=現在時刻`、`GmailNextRetryAtUtc=GmailLastAttemptedAtUtc+60分`を保存する。
22. Gmail再試行は`GmailDeliveryStatus=Failed`、`GmailAttemptCount=1`、再試行時刻到来、Gmail有効、認証済み、候補が現在も有効という条件を満たす場合だけ、次の正常取得を契機として1回実行する。専用の短時間再試行タイマーは追加しない。
23. 2回目も失敗した場合は`GmailAttemptCount=2`、`GmailDeliveryStatus=Failed`、`GmailNextRetryAtUtc=null`とし、それ以上自動再試行しない。
24. `invalid_grant`、401、権限取消、リフレッシュトークン失効ではPhase 4Bの認証サービスを`ReauthenticationRequired`へ移行し、自動再試行しない。不正な送信先、OAuthクライアント設定不備、Gmail API未有効化、恒久的な403も自動再試行しない。一時通信障害では資格情報を削除しない。
25. 同じ正常取得に新規候補と再試行候補が複数ある場合は1通へ集約する。成功時は全候補を`Succeeded`とし、新規候補は1回目、再試行候補は2回目として候補別試行回数を保存する。
26. Early／Standard／Finalは現在の時間帯が次段階へ進んだ場合、短期回復は現在残量が回復閾値未満の場合、リセット完了は現在の`RecoveryWindowId`が異なる場合に、古いGmail失敗状態を`Expired`として再試行しない。`resetsAt`のない使用率低下推定は同じ推定イベントかつ24時間以内であることを確認する。
27. Gmailの`InProgress`が最終試行から60分以上残った場合は、送信成功を確認できない中断として`Failed`へ戻す。60分未満では再送せず、`AttemptCount`を巻き戻さず、最大2回を超えない。
28. `ApplicationState`へ現在のGmail配送有効期間を示す`GmailDeliveryEnabledSinceUtc`を保存する。Gmailをfalseからtrueへ変更したとき、および`ReauthenticationRequired`から再認証へ成功したときに現在UTC時刻へ更新する。
29. 本番Gmail配送は`ConditionMetAtUtc >= GmailProductionDeliveryStartedAtUtc`かつ`ConditionMetAtUtc >= GmailDeliveryEnabledSinceUtc`を満たす通知だけを対象とする。Gmail無効期間、認証失効期間、認証解除期間に成立した通知、および認証系失敗になった古い通知を後から自動送信しない。
30. Gmail再試行ではWindows配送状態を変更・再送せず、Windows再試行ではGmail配送状態を変更・再送しない。
31. `GmailAuthenticationState.Error`、認証状態取得例外、一時通信障害など認証可否を確定できない状態では、直前の`GmailAuthenticationWasUsable`と`GmailDeliveryEnabledSinceUtc`を維持する。
32. `GmailAuthenticationWasUsable=false`へ変更するのは`Unauthenticated`、`ReauthenticationRequired`、明示的な認証解除・権限失効に限る。一時障害からの回復を再認証完了として記録しない。

### FR-013 長期枠のリセット前通知

Weeklyなどの長期枠について、リセット前通知を有効にできる。初期値は有効とする。

| 通知種別 | 通知段階 | 有効な残り時間 | 残量条件 |
|---|---|---:|---:|
| LongWindowEarlyWarning | Early | 48時間以内、24時間より前 | 50%以上 |
| LongWindowStandardWarning | Standard | 24時間以内、6時間より前 | 20%以上 |
| LongWindowFinalWarning | Final | 6時間以内、リセット時刻より前 | 10%以上 |

1. 各閾値と残り時間は設定で変更できる。
2. 同じ利用枠・リセット期間・通知段階につき最大1回だけ送る。
3. 対象段階の有効な残り時間を過ぎた通知は送らない。
4. 取得時点で複数段階の条件が重なる場合の扱いは、段階の時間帯が重ならないよう上表の範囲で判定する。
5. 未使用分が次の利用期間へ繰り越されることは確認できていないため、通知文では繰り越しまたは消滅を断定しない。
6. 通知文では、リセット前に残量を確認できるよう通知していることを示す。
7. `resetsAt`が存在しない場合は残り時間を推定せず、Early、Standard、Finalの候補にしない。

### FR-014 自動再接続

Codex App Serverまたは利用枠取得に失敗した場合、次の処理を行う。

1. 失敗回数を連続失敗回数として記録する。
2. 再試行間隔は段階的に延長する。
   - 1回目：1分後
   - 2回目：5分後
   - 3回目以降：15分後
3. 3回連続で失敗した場合、Windows通知を1回表示する。
4. Gmailが利用可能な場合でも、監視異常のGmail通知は初版では送らない。
5. 正常取得できた場合、連続失敗回数を0へ戻す。
6. 復旧後は通常の監視スケジュールへ戻る。
7. 同一障害中にエラー通知を繰り返さない。

### FR-015 Windowsログイン時の自動起動

1. 初回設定時に有効・無効を選択できる。
2. 初期表示は有効とする。
3. 設定画面から後で変更できる。
4. 自動起動時はメインウィンドウを表示せず、タスクトレイに常駐する。
5. 自動起動の登録失敗時は、ユーザーへ理由を表示する。
6. 管理者権限を必須としない方式を優先する。
7. `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`へ、登録名`Codex Usage Notifier`で現在の実行ファイル絶対パスを引用符付きで登録する。
8. 登録値へ固定引数`--autostart`を付加し、Windowsログイン時は状態画面を表示せずトレイだけへ常駐する。ユーザー入力文字列はコマンドラインへ結合しない。
9. 設定画面で変更すると、OS登録を先に同期してから設定JSONを保存する。OS変更失敗時は設定を保存せず、設定保存失敗時はOS状態を変更前へ戻す。ロールバック失敗時は不一致を明示する。
10. 起動時は`AppSettings.AutoStartEnabled`を正としてOS登録を同期する。同期失敗は非致命とし、ログと設定画面へ表示して監視を継続する。
11. 設定画面へ登録済み、未登録、不一致、登録不可、確認エラーを表示する。
12. `dotnet.exe`、および`publish`以外の`bin\Debug`／`bin\Release`配下からの開発実行は登録を拒否し、配布用exeから設定するよう案内する。実行ファイルパス取得はテストで差し替え可能にする。
13. Registry操作はCurrentUserだけを使用し、Presentation層から直接呼び出さない。

### FR-016 状態表示画面

初版では次を表示する。

| 表示項目 | 内容 |
|---|---|
| 5時間枠候補 | 存在しない場合は「5時間枠：未観測」 |
| すべての利用枠 | 全LimitId、Position、Classification、期間、使用率、残量、次回リセット時刻、リセットまでの残り時間 |
| 通知設定 | 各枠の通知設定が有効か、および有効な通知種類 |
| リセット情報 | `resetsAt`を取得できているか、次回リセット時刻、リセットまでの残り時間。未取得時は「リセット時刻未取得」 |
| 枠別通知状態 | 各枠の最終Windows通知と最終Gmail通知、最後のリセット完了判定理由、回復連番 |
| 利用可能リセットクレジット数 | `rateLimitResetCredits.availableCount`。通常の周期的リセット回数ではない |
| 監視状態 | 正常、取得中、再接続中、エラー |
| 最終取得 | 最終成功時刻 |
| 次回確認 | 予定時刻 |
| Gmail通知 | 有効、無効 |
| Gmail認証 | 未設定、未認証、認証済み、再認証必要、認証エラー、および認証済みアカウント |
| 最終通知 | WindowsとGmailそれぞれの通知時刻と送信結果 |
| 連続失敗 | 現在の失敗回数 |

利用履歴グラフは初版に含めないが、履歴データは将来のグラフ表示に利用できる形式で保存する。

実アカウントで週間枠だけを取得した場合の表示例は次のとおりとする。

```text
週間枠
LimitId：codex
位置：Primary
分類：Weekly
期間：10080分
使用率：35%
残量：65%
5時間枠：未観測
```

### FR-017 設定画面

タスクトレイの「設定」と状態画面からWPF設定画面を開き、次を設定できる。

- FiveHourへ適用する短期枠回復通知の既定有効状態と残量閾値
- Weeklyへ適用するEarly、Standard、Final、リセット完了通知それぞれの既定有効状態
- 早期通知の残量閾値と残り時間
- 通常通知の残量閾値と残り時間
- 最終通知の残量閾値と残り時間
- Windows通知の有効・無効
- Gmail通知の有効・無効
- Gmail送信先
- 通知禁止時間の有効・無効、開始時刻、終了時刻
- Windowsログイン時の自動起動設定値
- 補助確認間隔（分）

Phase 4Bでは、OAuthクライアント設定の状態と標準配置先、認証状態、認証済みGoogleアカウント、最終認証成功時刻、再認証要否、最終テスト送信結果を表示する。「OAuthクライアント設定ファイルを選択」「Googleアカウントで認証」「再認証」「認証解除」「テストメール送信」を提供する。OAuth設定がない場合は認証、未認証または送信先不正の場合はテスト送信を無効化し、認証中・送信中の同じ操作を重複実行しない。

認証成功後に`GmailRecipient`が空なら認証済みアドレスを初期入力する。Gmail通知は初期値を無効とし、認証済みかつ送信先が有効な場合だけtrueとして保存できる。画面には、Google認証、テスト送信、Phase 4C-1／4C-2の本番利用枠通知と一時障害再試行を利用できることを明記する。自動起動はPhase 5AでWindowsへ接続し、設定値とOS登録状態を別々に表示する。

取得済みの各利用枠について、LimitId、Position、WindowDurationMinutes、Classification、適用される通知設定、通知有効状態を表示する。FiveHourとWeeklyには編集した分類別既定値を適用する。Unknownは表示するが設定画面から有効化せず、「利用期間の意味を識別できないため、通知対象外です」と表示する。LimitId、Position、WindowDurationMinutesが一致する既存の利用枠別上書き設定は保持する。

#### 入力検証

1. 短期・長期の残量閾値は1～100%とする。
2. Early、Standard、Finalの残り時間は正の整数とする。
3. 残り時間は`Early > Standard > Final`の順とする。初期値は48時間、24時間、6時間とする。
4. Gmail送信先は、入力されている場合だけメールアドレス形式とする。
5. 補助確認間隔は1～1440分とする。
6. 通知禁止時間は`HH:mm`形式とし、開始時刻が終了時刻より後の日付をまたぐ設定を許容する。
7. 不正値がある場合は保存ボタンを無効にし、該当入力の近くへ理由を文字で表示する。

#### 編集と保存

1. 「保存」「キャンセル」「初期値へ戻す」を提供する。
2. キャンセルは未保存変更を破棄して最後に読み込んだ設定へ戻す。
3. タイトルバーから未保存変更がある画面を閉じる場合は、破棄確認を表示する。
4. 初期値へ戻す操作だけでは保存せず、保存ボタンで確定する。
5. 設定ファイルは一時ファイルへの書き込み後に置換する。
6. ファイルI/OはUIスレッドで実行しない。
7. 保存成功後、補助確認とリセット確認を再設定し、状態画面へ新設定を反映する。
8. 保存だけを契機とした利用枠の即時取得や通知判定は行わず、次の正常取得から新設定を使用する。
9. 設定保存時に通知済み状態、回復連番、利用枠履歴を初期化しない。
10. 保存失敗時は編集前の永続設定を維持し、エラー理由を表示する。
11. Tab、Enter、Escapeによるキーボード操作を可能とし、入力エラーは色だけで表現しない。
12. OAuth設定ファイルI/O、DPAPI、OAuth通信、Gmail API通信はUIスレッドで実行しない。
13. 認証解除でGmail通知設定だけを即時無効化しても、他の未保存編集、通知済み状態、回復連番、利用枠履歴を消去しない。
14. 自動起動設定を変更する場合はOS状態を先に同期し、設定保存失敗時は変更前のOS状態へロールバックする。

### FR-018 履歴保存

1. 利用枠の取得成功ごとに、取得できたすべての利用枠を履歴へ保存する。
2. 保存形式は、取得1回を1行とするJSON Linesとする。
3. 各利用枠に次を保存する。
   - 取得時刻（UTC）
   - LimitId
   - Position
   - WindowDurationMinutes
   - UsedPercent
   - ResetsAtUtc
   - Classification
4. 初期保存期間は90日とする。
5. 起動時または1日1回、保存期間を超えた履歴を削除する。
6. 1レコードの破損で全履歴を読み込めなくならない形式とする。
7. 将来、SQLiteへ移行できるよう永続化処理を抽象化する。
8. 過去履歴に存在しないLimitId、Position、WindowDurationMinutesの組み合わせを初観測として検出し、ログへ記録する。
9. 保持対象は`CapturedAtUtc >= 現在UTC - HistoryRetentionDays`とし、取得1回のJSONL行を単位として扱う。
10. 保持対象行を同一ディレクトリの一時ファイルへ書き、flush後に原子的に置換する。保守失敗・キャンセル時は元ファイルを維持する。
11. JSONとして解釈できない破損行は無言で削除せず、警告ログを残してそのまま保持する。
12. 追記と保守は`JsonUsageHistoryRepository`内部の同じ排他を使用し、追記直後の行を古い内容で上書きしない。
13. 保守成功後は保持された正常行だけからobservedKeysを再構築する。90日以上観測されなかった組み合わせは再登場時に新規として検出する。
14. `HistoryRetentionDays`は7～3650とし、設定ファイルの範囲外値は他の有効設定を維持したまま90へ補正する。

### FR-019 状態保存

`state.json`には少なくとも次を保存する。

- 利用枠・リセット期間・通知種別・通知段階ごとの通知状態
- 利用枠ごとの閾値未満状態、直近残量、回復連番
- 長期枠リセット完了の判定理由
- Windows通知とGmail通知それぞれの送信結果
- Phase 4Cの本番Gmail配送開始時刻`GmailProductionDeliveryStartedAtUtc`
- 現在のGmail配送有効期間の開始時刻`GmailDeliveryEnabledSinceUtc`
- Gmailの再試行可否を示す安全な失敗分類と、直近のGmail有効・認証利用可能状態
- 条件成立時刻と送信時刻
- 保留通知と保留終了時刻
- 最終取得成功時刻
- 最終取得値
- 連続失敗回数
- 障害通知済みフラグ
- 初回設定完了フラグ
- 最後に履歴・ログ保守を試行したUTC時刻`LastMaintenanceAtUtc`

状態ファイルは一時ファイルへ書き込んだ後に置換し、書き込み途中の破損を防止する。

状態読込前にルートの`SchemaVersion`を検証する。現在版と同じ場合は通常どおり読み込み、現在版より古い場合は明示的にサポートする1段階ごとのmigrationだけを順番に実行して保存する。現在版より新しい場合は、元ファイルの内容、更新日時、名前、配置を変更せず、初期値や現在版で置換せずに起動を中止する。future schema拒否後は監視、App Server、Gmail、Windows通知判定を開始せず、保存版と対応版を含む安全な案内とログを出力する。

OAuthトークンと認証メタデータは`state.json`や`settings.json`へ混在させず、FR-011のDPAPI保護ストアへ分離する。Phase 4Bのテストメールは`state.json`を更新しない。`GmailProductionDeliveryStartedAtUtc`は機密情報を含まない本番配送境界として`state.json`へ保存し、再起動後も変更しない。

Phase 5Aでは状態スキーマをVersion 4へ進め、Version 3から`LastMaintenanceAtUtc=null`を追加する明示的なmigrationを実行する。既存のVersion 1→2→3の段階migrationとfuture version無変更拒否を維持する。

### FR-020 ログ

通常ログには次を記録する。

- アプリ起動・終了
- App Server起動・終了
- 利用枠取得の成否
- 取得した残量
- 短期枠回復、長期枠リセット前、長期枠リセット完了の判定結果
- 通知種別、通知段階、および重複抑止結果
- Windows通知の成否
- テスト通知の種類と送信成否
- Gmail通知の成否
- OAuthクライアント設定の読み込み
- OAuth認証の開始、成功、キャンセル、失敗
- アクセストークン更新と再認証必要状態への移行
- 認証解除のGoogle側失効結果とローカル削除結果
- Gmailテスト送信の開始、成功、失敗
- Gmail 401、権限不足403、API未有効化403、恒久403の安全な分類
- 一時認証状態取得エラーと、確定した再認証必要状態の区別
- future state schemaの保存版・対応版と起動中止
- 再試行と復旧
- 設定変更
- 履歴削除
- 自動起動の登録、削除、起動時同期失敗
- 履歴保守の削除・保持・破損行保持件数
- ログ保守の削除・失敗件数
- 運用保守の完了と非致命エラー

`access_token`、`refresh_token`、`id_token`、認証コード、クライアントシークレット、Authorizationヘッダー、Cookie、MIMEメール全文、OAuth応答本文をログへ出力しない。例外は安全な分類と概要だけをログへ渡す。メールアドレスをログへ記録する場合は、`ex***@gmail.com`のようにローカル部を部分マスクする。

初期ログ保持期間は30日とし、設定値は7～3650とする。範囲外のファイル値は他の有効設定を維持したまま30へ補正する。

`codex-usage-notifier-yyyyMMdd.log`へ完全一致し、実在する日付を持ち、`現在日 - LogRetentionDays`より古いファイルだけを削除する。当日、前日、形式違い、不正日付、別名ログは削除しない。1ファイルの削除失敗は他の対象と監視処理を停止させない。

### FR-021 長期枠のリセット完了通知

Weeklyなどの長期枠について、新しい利用期間の開始を`LongWindowResetCompleted`として通知できる。初期値は有効とする。

1. `resetsAt`到達時は、初期値として60秒後に利用枠を再取得する。
2. タイマーが`resetsAt`へ到達しただけでは、リセット完了または通知済みと確定しない。
3. 再取得後、次のいずれかを確認した場合に新しいリセット期間と判定する。
   - `resetsAt`が次の期間の値へ変化した場合は`ResetTimeAdvanced`とする。
   - 同一のLimitId、Position、WindowDurationMinutesについて、前回の正常取得から`usedPercent`が設定値`ResetInferenceUsageDropPoints`以上低下した場合は`UsageDropInference`とする。
4. 新しい期間を確認できない場合は通知せず、補助確認または更新通知による次回取得を待つ。
5. 同じ新しいリセット期間について最大1回だけ送る。
6. 通知禁止時間中は保留し、禁止時間終了後に再取得して新しい期間を確認してから送る。
7. `resetsAt`がない場合も`UsageDropInference`を適用できる。推定閾値の初期値は50ポイントとする。
8. 判定理由は通知状態と診断ログへ保存する。

### FR-022 運用保守

1. UIやUsageMonitorへ履歴・ログ削除を直接実装せず、`IApplicationMaintenanceService`、`IUsageHistoryMaintenance`、`ILogMaintenance`へ分離する。
2. UI・タスクトレイ・UsageMonitorの開始後、保守をバックグラウンドで開始する。保守完了を待って監視開始を遅らせない。
3. `LastMaintenanceAtUtc`がない初回、または前回試行から24時間以上経過した場合だけ履歴とログを保守する。
4. 起動時・日次・将来の追加トリガーが重なっても`SemaphoreSlim`でsingle-flightにし、後続要求は更新済み時刻を再確認する。
5. 履歴保守が失敗してもログ保守を試み、ログ保守が失敗してもCodex App Server監視、Windows通知、Gmail通知、設定画面、トレイ常駐を継続する。
6. 保守を試行した場合は個別結果にかかわらず`LastMaintenanceAtUtc`を更新し、同じ失敗を短時間に大量記録しない。状態保存自体の失敗時は1時間後に期限を再確認する。
7. 保守中のアプリ終了はCancellationTokenで中止し、バックグラウンドTaskを放置しない。
8. 保守結果は件数だけをログへ記録し、履歴内容をログへ出力しない。

## 7. 非機能要件

### NFR-001 信頼性

- 一時的な通信エラーでアプリ全体を終了しない。
- 未知のJSONフィールドを無視できる。
- 不正なレスポンスを検出してログへ記録する。
- 同じ利用枠・リセット期間・通知種別・通知段階で通知を乱発しない。
- PC再起動後も通知済み状態を維持する。
- 現在より新しい状態スキーマを古いアプリで変更しない。
- 同じWindowsユーザーで複数の監視プロセスを実行しない。

### NFR-002 セキュリティ

- Gmailのパスワードを扱わない。
- OAuthトークンを平文保存しない。
- 認証情報をGitへコミットしない。
- 外部プロセス起動時に、ユーザー入力をそのままコマンドラインへ連結しない。
- JSON-RPCログには認証情報を含めない。
- 通知メールにはソースコードや機密情報を含めない。
- OAuthトークンはDPAPI CurrentUserで暗号化し、平文ファイルデータストアを使用しない。
- OAuthクライアント設定、認証情報、および実トークン値をGit追跡対象にしない。
- 認証情報をViewModel、`AppSettings`、画面表示用モデルへ露出しない。

### NFR-003 性能

- 待機中のCPU使用率は実用上無視できる水準を目標とする。
- 待機中に高頻度ポーリングを行わない。
- 取得処理が長時間応答しない場合はタイムアウトする。
- UIスレッドで外部プロセス通信やネットワーク処理を行わない。
- UIスレッドで設定ファイルの読み書きを行わない。

### NFR-004 保守性

- Codex通信、通知判定、Windows通知、Gmail通知、永続化を分離する。
- 外部サービスはインターフェース越しに利用する。
- 各クラス、メソッド、プロパティに日本語コメントを付ける。
- 公開APIと複雑な判定処理には、目的と前提条件をコメントする。
- 設定ファイルのスキーマ変更にバージョンを持たせる。

### NFR-005 テスト容易性

時刻、外部プロセス、通知、ファイルI/Oを抽象化し、単体テストで差し替え可能にする。

## 8. データモデル案

### 8.1 UsageSnapshot

| プロパティ | 型 | 説明 |
|---|---|---|
| CapturedAtUtc | DateTimeOffset | 取得時刻 |
| RateLimits | IReadOnlyList&lt;RateLimitWindow&gt; | App Serverから取得したすべての利用枠 |
| FiveHourCandidate | RateLimitWindow? | 最初に観測された300分枠。存在しない場合はnull |
| WeeklyCandidate | RateLimitWindow? | 最初に観測された10080分枠。存在しない場合はnull |
| ResetCredits | int? | App Serverの`rateLimitResetCredits.availableCount`由来の利用可能リセットクレジット数。JSON互換性のため内部名を維持し、通常の周期的リセット回数とは解釈しない |
| Trigger | UsageCheckTrigger | 取得契機 |

### 8.2 RateLimitWindow

| プロパティ | 型 | 説明 |
|---|---|---|
| UsedPercent | double | 使用率 |
| RemainingPercent | double | 残量 |
| WindowDurationMinutes | int? | ウィンドウ長 |
| ResetsAtUtc | DateTimeOffset? | リセット時刻 |
| Classification | RateLimitClassification | FiveHour、Weekly、Unknownの識別結果 |
| LimitId | string? | App Serverが返したlimitId |
| LimitName | string? | App Serverが返した表示名 |
| Position | RateLimitPosition | PrimaryまたはSecondaryの位置 |
| PlanType | string? | App Serverが返したプラン種別 |
| RateLimitReachedType | string? | App Serverが返した利用枠到達理由 |

### 8.3 RateLimitNotificationState（概念モデル）

次のモデルはPhase 3で使用する通知状態モデルを表す。

| プロパティ | 型 | 説明 |
|---|---|---|
| LimitId | string | App Serverが返したlimitId |
| Position | RateLimitPosition | PrimaryまたはSecondaryの位置 |
| WindowDurationMinutes | int | ウィンドウ長 |
| RecoveryWindowId | string | リセット期間ID。既存名称との互換を考慮した概念名 |
| NotificationType | NotificationType | 通知種別 |
| NotificationStage | NotificationStage | 通知段階 |
| ConditionMetAtUtc | DateTimeOffset | 条件成立時刻 |
| DeliveredAtUtc | DateTimeOffset? | いずれかの送信先へ最初に送信できた時刻 |
| WindowsDeliveryStatus | DeliveryStatus | Windows通知状態 |
| WindowsAttemptCount | int | Windows通知の累計表示試行回数 |
| WindowsLastAttemptedAtUtc | DateTimeOffset? | Windows通知の最終表示試行時刻 |
| WindowsNextRetryAtUtc | DateTimeOffset? | Windows通知の次回再試行時刻 |
| GmailDeliveryStatus | DeliveryStatus | Gmail通知状態 |
| GmailAttemptCount | int | Gmail通知の累計送信試行回数 |
| GmailLastAttemptedAtUtc | DateTimeOffset? | Gmail通知の最終送信試行時刻 |
| GmailNextRetryAtUtc | DateTimeOffset? | Gmail通知の次回再試行時刻 |
| GmailFailureKind | GmailDeliveryFailureKind | None、Transient、Authentication、Permanent、Interruptedの安全な失敗分類 |
| DeferredUntilUtc | DateTimeOffset? | 保留終了時刻 |
| ResetCompletionReason | ResetCompletionReason? | ResetTimeAdvancedまたはUsageDropInference |

永続化キーはLimitId、Position、WindowDurationMinutes、RecoveryWindowId、NotificationType、NotificationStageの組み合わせとする。送信先ごとの成功・失敗を別々に保持し、一方の失敗によって成功済みの送信先へ重複送信しない。

`ApplicationState`はこれらの候補別状態とは別に、`GmailProductionDeliveryStartedAtUtc: DateTimeOffset?`と`GmailDeliveryEnabledSinceUtc: DateTimeOffset?`を保持する。前者は初回Phase 4C起動時だけ現在UTC時刻を設定し、後者はGmailのfalseからtrueへの変更または再認証成功時に更新する。Phase 4B以前、Gmail無効期間、認証失効・認証解除期間の通知は、保存済み`ConditionMetAtUtc`がいずれかの境界より前なら本番配送しない。設定と認証状態の変化を次の正常取得でも検出できるよう、直近のGmail有効状態と認証利用可否を機密情報を含まない状態として保存する。

### 8.4 RateLimitRecoveryState

`resetsAt`がない短期枠の閾値遷移を再起動後も継続するため、次を利用枠ごとに保存する。

| プロパティ | 型 | 説明 |
|---|---|---|
| LimitId | string | App Serverが返したlimitId |
| Position | RateLimitPosition | PrimaryまたはSecondaryの位置 |
| WindowDurationMinutes | int | ウィンドウ長 |
| HasObservation | bool | 正常な残量を観測済みか |
| WasBelowThreshold | bool | 直近観測が回復閾値未満だったか |
| RecoverySequence | int | 閾値以上への回復連番 |
| LastRemainingPercent | double | 直近観測の残量 |

### 8.5 RateLimitNotificationSetting

利用枠別設定はLimitId、Position、WindowDurationMinutesで識別し、ShortWindowRecovered、Early、Standard、Final、LongWindowResetCompletedをそれぞれ有効化できる。保存設定がない枠にはClassification別の既定値を適用する。

| プロパティ | 型 | 説明 |
|---|---|---|
| LimitId | string | App Serverが返したlimitId |
| Position | RateLimitPosition | PrimaryまたはSecondaryの位置 |
| WindowDurationMinutes | int | ウィンドウ長 |
| ShortWindowRecoveryEnabled | bool | 短期枠回復通知の有効状態 |
| LongWindowEarlyWarningEnabled | bool | Early通知の有効状態 |
| LongWindowStandardWarningEnabled | bool | Standard通知の有効状態 |
| LongWindowFinalWarningEnabled | bool | Final通知の有効状態 |
| LongWindowResetCompletedEnabled | bool | リセット完了通知の有効状態 |

保存設定がない場合の既定値は、FiveHourがShortWindowRecoveryEnabledだけ有効、Weeklyが4種類の長期通知を有効、Unknownがすべて無効とする。保存設定がある場合は、Classificationにかかわらず指定された通知種類を適用する。

### 8.6 AppSettings

次の表はPhase 5A時点の設定モデルを示す。分類別既定値と画面項目は設定画面から編集でき、利用枠別上書き設定と非表示項目はJSON永続化時に保持する。OAuthトークンと認証済みアカウント情報は`AppSettings`へ含めない。

| プロパティ | 初期値 |
|---|---:|
| CodexExecutablePath | codex |
| RateLimitNotifications | 空配列。観測枠にはClassification別既定値を適用 |
| ShortWindowRecoveryEnabled | true |
| ShortWindowRecoveryThresholdPercent | 99 |
| LongWindowEarlyWarningEnabled | true |
| LongWindowEarlyWarningThresholdPercent | 50 |
| LongWindowEarlyWarningHours | 48 |
| LongWindowStandardWarningEnabled | true |
| LongWindowStandardWarningThresholdPercent | 20 |
| LongWindowStandardWarningHours | 24 |
| LongWindowFinalWarningEnabled | true |
| LongWindowFinalWarningThresholdPercent | 10 |
| LongWindowFinalWarningHours | 6 |
| LongWindowResetCompletedEnabled | true |
| ResetInferenceUsageDropPoints | 50。画面には表示せず1～100の範囲で保持 |
| WindowsNotificationEnabled | true |
| GmailNotificationEnabled | false。認証済みかつGmailRecipientが有効な場合だけtrueを保存可能。本番配送と再試行はPhase 4C-1／4C-2で実装済み |
| GmailRecipient | null。入力時はメールアドレス形式 |
| QuietHoursEnabled | true |
| QuietHoursStart | 00:00 |
| QuietHoursEnd | 07:00 |
| FallbackPollingMinutes | 60 |
| ResetCheckDelaySeconds | 60 |
| HistoryRetentionDays | 90。設定ファイル上の許容範囲は7～3650。Phase 5Aでは画面非表示 |
| LogRetentionDays | 30。設定ファイル上の許容範囲は7～3650。Phase 5Aでは画面非表示 |
| AutoStartEnabled | 初回設定で選択、初期表示はtrue。Phase 5AでCurrentUser Runキーへ接続 |

`ResetInferenceUsageDropPoints`、`HistoryRetentionDays`、`LogRetentionDays`がそれぞれの範囲外の場合は、その項目だけを既定値へ補正し、他の有効な設定値は維持する。保持日数と推定閾値はPhase 5Aでも一般ユーザー向け画面から変更できない。

### 8.7 GmailAuthenticationStatus（画面用モデル）

トークンを含めず、認証専用サービスが次の安全な状態だけを画面へ公開する。

| プロパティ | 型 | 説明 |
|---|---|---|
| State | GmailAuthenticationState | NotConfigured、Unauthenticated、Authenticating、Authenticated、RefreshRequired、ReauthenticationRequired、Error |
| AuthenticatedEmailAddress | string? | 認証済みアカウント。トークンではない |
| LastAuthenticatedAtUtc | DateTimeOffset? | 最終認証成功時刻 |
| LastTokenRefreshedAtUtc | DateTimeOffset? | 最終トークン更新時刻 |
| LastErrorSummary | string? | 機密情報を含まない概要 |
| HasClientConfiguration | bool | OAuthクライアント設定の有無 |
| CanSendTestMail | bool | 認証状態から見たテスト送信可否 |
| RequiresReauthentication | bool | 再認証要否 |

### 8.8 GmailCredentialEnvelope（暗号化前の概念モデル）

| プロパティ | 型 | 説明 |
|---|---|---|
| SchemaVersion | int | 初期値1。将来のマイグレーション判定に使用 |
| GoogleDataStoreEntries | Dictionary&lt;string, string&gt; | Google公式クライアントが保存するTokenResponse等のJSON |
| CredentialMetadata | GmailCredentialMetadata | 認証済みメールアドレス、最終認証成功時刻、最終更新時刻 |

この概念モデル全体をメモリ上でJSON化した後、DPAPI CurrentUserで暗号化する。ディスクへ書き込む`google-oauth-credentials.dat`は暗号文であり、平文JSON、トークン断片、JWT本文を含めない。

### 8.9 Phase 5A運用状態

| モデル／値 | 内容 |
|---|---|
| AutoStartStatus | Registered、NotRegistered、Mismatch、Unsupported、Errorと安全な説明。Registryの生例外は画面へ出さない |
| UsageHistoryMaintenanceResult | 削除正常行数、保持全行数、保持した破損行数 |
| LogMaintenanceResult | 削除ファイル数、削除失敗ファイル数 |
| ApplicationState.LastMaintenanceAtUtc | 履歴・ログ保守を最後に試行したUTC時刻。SchemaVersion 4で追加 |

## 9. 通知判定フロー

```text
利用枠取得成功
   │
   ├─ すべての利用枠について利用枠別通知設定を解決
   │      ├─ FiveHour既定：短期枠回復だけ有効
   │      ├─ Weekly既定：Early／Standard／Final／リセット完了を有効
   │      └─ Unknown既定：すべて無効
   │
   ├─ 短期枠回復が有効な各枠
   │      ├─ resetsAtあり：残量99%以上ならShortWindowRecovered候補
   │      └─ resetsAtなし：閾値未満→以上の遷移ごとに回復連番を増加
   │
   ├─ 長期枠通知が有効な各枠
   │      ├─ resetsAtあり：48～24時間前・残量50%以上ならEarly候補
   │      ├─ resetsAtあり：24～6時間前・残量20%以上ならStandard候補
   │      ├─ resetsAtあり：6時間前～リセット前・残量10%以上ならFinal候補
   │      ├─ resetsAtなし：リセット前通知は候補にしない
   │      └─ リセット時刻の前進、または設定閾値以上の使用率低下
   │             └─ リセット完了候補と判定理由を生成
   │
   ├─ 同じ利用枠・リセット期間・通知種別・段階で送信済み
   │      └─ チャネルごとに重複通知なし
   │
   ├─ 通知禁止時間内
   │      └─ 保留状態を保存
   │
   └─ 通知可能
          ├─ Windows未送信・再試行可能候補を1件のバルーンへ集約
          ├─ Gmail開始境界以降の未試行候補を1通のメールへ集約
          ├─ WindowsとGmailを独立して配送
          └─ 候補ごとのチャネル別結果を状態保存

通知禁止時間終了
   │
   ├─ 利用枠を再取得
   ├─ リセット前通知は段階の期限と残量を再判定
   ├─ 期限切れのリセット前通知は未送信チャネルをExpiredへ変更
   └─ リセット完了通知は新しい期間を確認してからWindows／Gmailへ配送
```

## 10. 受け入れ条件

### AC-001 基本取得

- Codexへログイン済みの環境で、App Serverが返すすべてのlimitIdと利用枠を取得できる。
- 画面に全枠のPosition、Classification、残量、次回リセット時刻を表示できる。
- 300分枠が存在する場合、FiveHourの短期枠候補として分類できる。
- 300分枠がない場合も取得成功となり、「5時間枠：未観測」と表示できる。
- 10080分枠をWeeklyの週間枠候補として分類できる。
- 実観測済みのprimaryが10080分、secondaryがnullの週間枠だけの構成を処理・表示できる。

### AC-002 短期枠回復通知

- FiveHourの残量が前回98%、今回99%の場合に`ShortWindowRecovered`候補になる。
- 短期枠回復閾値を100%に変更した場合、99%では通知候補にならない。
- 同じ利用枠・リセット期間で再取得しても短期枠回復通知が重複しない。
- `resetsAt`がないFiveHourで残量が閾値未満から閾値以上へ遷移した場合、回復連番を増やして通知候補になる。
- `resetsAt`がなく閾値以上の状態が続く場合、同じ回復連番で重複通知しない。
- 一度閾値未満へ戻った後に再び閾値以上になった場合、新しい回復連番の通知候補になる。
- 過去状態がなく起動時点で閾値以上の場合、初回回復として最大1回だけ通知候補になる。

### AC-003 起動時通知

- PC起動時に短期枠回復または長期枠リセット前の条件を満たし、未通知なら通知候補になる。
- 同じリセット期間・通知種別・通知段階ですでに通知済みなら通知されない。

### AC-004 スリープ復帰

- スリープ復帰後に利用枠を再取得する。
- スリープ中に短期枠が回復した場合、または長期枠が通知段階へ入った場合、条件に応じて通知される。

### AC-005 通知禁止時間

- 01:00に通知条件を満たしても即時通知されない。
- 07:00以降に再取得して条件を再判定する。
- リセット前通知は、07:00時点で対象段階の期限と残量条件が有効な場合だけ送られる。
- 期限を過ぎたリセット前通知は送られない。
- リセット完了通知は、07:00以降に新しい利用期間を確認できれば送られる。
- 保留通知に条件成立時刻、送信時点の残量、通知段階が表示される。

### AC-006 長期枠リセット前通知

- リセットまで48時間以内かつ残量50%以上のWeekly枠が早期通知候補になる。
- リセットまで24時間以内かつ残量20%以上のWeekly枠が通常通知候補になる。
- リセットまで6時間以内かつ残量10%以上のWeekly枠が最終通知候補になる。
- 同じ利用枠・リセット期間・通知段階について複数回送られない。
- 各段階の有効時間を過ぎてから、期限切れの段階を遡って送らない。
- `resetsAt`がない長期枠は残り時間を推定せず、Early、Standard、Finalの候補にならない。

### AC-007 Gmail

- OAuthクライアント設定がない場合は`NotConfigured`となり、標準配置先と準備手順を表示して認証ボタンを無効にできる。
- 不正なOAuthクライアントJSONを拒否し、既存の有効設定を上書きしない。
- 有効なデスクトップアプリ用JSONを標準配置先へ登録し、システム既定ブラウザー、PKCE、ループバックで認証できる。
- OAuth同意画面で要求する権限が`gmail.send`、`openid`、`email`に限定される。
- OAuth認証後、認証済みアドレスを表示し、空の送信先へ初期入力できる。
- 認証済みかつ送信先が有効な場合だけGmail通知設定とテスト送信を有効にできる。
- 同じGmailアドレスまたは別の有効な送信先へ、日本語の件名・本文を持つテストメールを`users.messages.send`で送信できる。
- OAuth認証とテスト送信をそれぞれ同時に最大1件へ制限できる。
- アクセストークン期限切れ時にリフレッシュトークンで自動更新し、通常の期限切れでは再認証を要求しない。
- `invalid_grant`、権限取消、401、資格情報破損・復号失敗時に再認証が案内され、一時通信障害では認証情報を削除しない。
- 認証解除でGoogle側失効を試み、結果にかかわらずローカル削除を試行し、成功時はGmail通知をfalseへ保存する。
- 認証情報ファイルが平文JSONでなく、同じWindowsユーザーのDPAPI CurrentUserでのみ復号できる。
- テスト送信の成功・失敗によって本番通知状態、Windows/Gmail配送状態、試行回数、回復連番、利用枠履歴が変化しない。
- ログとGit追跡対象へOAuthトークン、認証コード、クライアントシークレット、MIME全文が含まれない。
- Phase 4Bのテストメール送信経路からは本番の利用枠通知メールを送信しない。

### AC-008 再接続

- App Serverを停止しても、アプリが異常終了しない。
- 3回連続失敗時にWindows通知が1回だけ表示される。
- App Server復旧後に監視が自動再開する。

### AC-009 永続化

- アプリ再起動後も、同じ利用枠・リセット期間・通知種別・通知段階の通知が重複しない。
- 90日を超えた履歴が削除される。
- 破損した履歴1行があっても、他の履歴を処理できる。

### AC-010 自動起動

- 初回設定で自動起動を選択できる。
- 有効時はWindowsログイン後にタスクトレイへ起動する。
- 無効化した場合、次回ログイン時に起動しない。

### AC-011 長期枠リセット完了通知

- リセット予定時刻へ到達しただけでは通知済み状態にならない。
- リセット予定時刻後に利用枠を再取得する。
- `resetsAt`が次の期間へ進んだ場合、`ResetTimeAdvanced`を理由としてリセット完了通知候補になる。
- 前回と今回の正常取得で同一利用枠の`usedPercent`が50ポイント以上低下した場合、`resetsAt`の有無にかかわらず`UsageDropInference`を理由としてリセット完了通知候補になる。
- `usedPercent`の低下が49ポイント以下の場合、使用率低下だけではリセット完了通知候補にならない。
- 同じ新しいリセット期間についてリセット完了通知が重複しない。

### AC-012 Unknown枠と任意構成

- Unknown枠を破棄せず、状態画面へ表示して履歴保存できる。
- Unknown枠を通知対象にする設定の初期値が無効である。
- primaryを短期枠、secondaryを長期枠と決めつけず、WindowDurationMinutesで分類できる。
- 週間枠だけが存在する実アカウント相当のレスポンスをエラーなく処理できる。

### AC-013 複数利用枠の同時通知

- FiveHourとWeeklyが同時に存在する場合、両方を同時に通知対象にできる。
- FiveHourの短期枠回復とWeeklyの各長期枠通知を独立して判定できる。
- 一方の利用枠の通知済み状態が、別の利用枠の通知候補を抑止しない。
- 利用枠別設定がない場合はClassification別の既定値を適用する。

### AC-014 テスト通知

- タスクトレイから短期枠回復、Early、Standard、Final、リセット完了、監視障害を個別に送信できる。
- テスト通知の送信前後で、本番の通知済み状態、回復連番、利用枠履歴が変化しない。
- テスト通知の種類と送信結果をログへ記録する。
- テスト通知をクリックすると状態画面が開く。

### AC-015 Phase 3.1状態表示

- 取得した各枠について、通知設定の有効状態、有効な通知種類、`resetsAt`取得状態、リセットまでの残り時間を表示できる。
- 取得した各枠について、最後に送信した通知、最後のリセット完了判定理由、回復連番を表示できる。
- `resetsAt`がない枠には「リセット時刻未取得」、既定のUnknown枠には「通知対象外」と表示できる。

### AC-016 設定画面

- タスクトレイと状態画面の両方から設定画面を開ける。
- 保存済み設定を読み込み、編集、理由付き検証、保存、キャンセル、初期値復元ができる。
- 1%と100%の通知閾値を保存でき、0%と101%は保存できない。
- Early、Standard、Finalの残り時間が正で、かつ降順の場合だけ保存できる。
- 通知禁止時間に日付をまたぐ開始・終了時刻を保存できる。
- Gmail送信先は未入力またはメールアドレス形式の場合だけ保存できる。
- Gmail未認証ではGmail通知を有効化できず、OAuth設定がなければ認証、認証済みでなければテスト送信を無効化できる。
- 取得済みのFiveHourとWeeklyへ編集した既定設定を表示し、Unknownは説明付きの通知対象外として表示する。
- 保存成功後は再起動せず監視スケジュールと状態表示へ反映し、次の正常取得から通知判定に使用する。
- 保存操作だけでは利用枠を即時取得せず、通知済み状態、回復連番、履歴を消去しない。
- 保存失敗時は元の永続設定を維持する。
- `ResetInferenceUsageDropPoints`の初期値が50で、不正なファイル値はこの項目だけ50へ補正される。
- 保存後にアプリを再起動しても設定値が維持される。

### AC-017 通知信頼性

- 同一取得で短期枠と長期枠の候補が同時成立した場合、1件のWindows通知へ集約され、各候補の状態が成功になる。
- Windows通知が失敗した場合、5分後から最大3回まで再試行し、成功後は再送しない。
- 5分以上残った`InProgress`を次の正常取得で再試行できる。
- Final時間帯でFinalが無効な場合はStandardを、Standard時間帯でStandardが無効な場合はEarlyを送らない。
- 保留終了前、24時間超過、現在期間と不一致の保留を送らない。
- 短時間に連続した更新通知を最後の1回へデバウンスできる。
- Windows無効・Gmail有効でもGmail本番通知を送信でき、Windows成功済み・Gmail未送信ならGmailだけを送信できる。

### AC-018 Phase 4Bテストメール境界

- Gmailテスト送信サービスが`RateLimitNotificationProcessor`から参照されず、本番通知候補の配送へ組み込まれていない。
- `GmailDeliveryStatus`、`GmailAttemptCount`、`GmailLastAttemptedAtUtc`、`GmailNextRetryAtUtc`を維持するが、Phase 4Bのテスト送信では更新しない。
- GmailテストメールはPhase 4C-1の本番Gmail通知送信サービスを呼び出さない。
- Gmail本番通知はPhase 4Bのテスト送信結果表示を変更しない。

### AC-019 Phase 4C-1 Gmail本番配送

- Gmail通知が有効、認証情報が利用可能、送信先が有効な場合だけ、共通通知候補をGmail APIへ本番配送できる。
- Windows無効・Gmail有効でもGmailを送信できる。
- Windows成功済み・Gmail未送信ならGmailだけを送り、Gmail成功済み・Windows未送信ならWindowsだけを送る。
- 同じ取得の複数候補と複数limitIdを1通へ集約できる。
- 集約成功時は各候補のGmail状態が`Succeeded`、失敗時は各候補が`Failed`かつ`GmailAttemptCount=1`になり、Windows状態を変更しない。
- 初回Phase 4C起動時に`GmailProductionDeliveryStartedAtUtc`を保存し、再起動後も同じ境界を維持できる。
- 保存済み`ConditionMetAtUtc`がGmail配送開始時刻より前の`NotAttempted`状態を遡って送信しない。
- 通知禁止時間中はGmailを送らず、終了後に現在も有効な候補だけを送る。
- 時間帯または残量条件を過ぎたEarly／Standard／Finalの未送信Gmail状態を`Expired`にし、古い段階を送らない。
- `ResetTimeAdvanced`と`UsageDropInference`を本文で区別し、後者が推定であることを明示できる。
- Gmail成功後は同じ候補を再送しない。
- Gmail初回失敗後の候補別状態をPhase 4C-2の再試行判定へ引き継げる。
- 401または`invalid_grant`で再認証必要状態へ移行し、一時通信障害では認証情報を削除しない。

### AC-020 Phase 4C-2 Gmail配送再試行

- 初回の一時失敗から60分後に`GmailNextRetryAtUtc`が設定され、60分未満では再試行しない。
- 60分後以降の次回正常取得で1回だけ再試行し、2回目の失敗後は再試行時刻を持たない。
- ネットワーク障害、タイムアウト、429、5xxだけを自動再試行し、401、`invalid_grant`、恒久403、API未有効化、設定不備、不正送信先を自動再試行しない。
- 複数の再試行候補、および新規候補と再試行候補を1通へ集約し、候補ごとの試行回数を維持できる。
- EarlyからStandard、StandardからFinalへ進んだ古い段階を`Expired`とし、現在段階だけを新しい候補として扱う。
- 短期回復の残量が閾値未満なら再試行せず、リセット完了は同じ新期間を表す場合だけ再試行できる。
- 通知禁止時間中はGmailを再試行せず、試行回数を増やさない。終了後の正常取得で有効性を再判定できる。
- 60分以上古いGmail`InProgress`を試行回数を維持して回復し、最大2回を超えない。60分未満では再送しない。
- Gmail無効期間、認証失効期間、認証解除期間の通知を、再有効化・再認証後に遡って送らない。
- Windows成功・Gmail失敗、Windows失敗・Gmail成功、片方のみ有効、両方有効の各状態を独立して扱い、一方の再試行で他方を再送しない。
- 成功済み候補を再送しない。

### AC-021 Future state compatibility

- `state.json`のSchemaVersionが現在版と同じ場合は、ファイルを書き換えず通常読み込みできる。
- 明示的にサポートする旧SchemaVersionを1段階ごとのmigrationで現在版へ移行できる。
- 現在より新しいSchemaVersionは拒否し、元ファイルの内容・更新日時・名前・配置を変更しない。
- future schemaでは安全な案内とログを出力し、監視、Codex App Server、Gmail、Windows通知判定を開始しない。
- 破損JSONに対する既存の退避・初期化処理は維持する。

### AC-022 Gmail authorization classification

- Gmail API 401と、`insufficientPermissions`等の明確な権限不足403を`Authentication`として`ReauthenticationRequired`へ移行し、自動再試行しない。
- `accessNotConfigured`と`serviceDisabled`はAPI未有効化の`Permanent`とし、再認証を要求しない。
- 未知の403を無条件に認証失効とせず、恒久拒否として自動再試行しない。
- 429、5xx、ネットワーク障害、タイムアウトは既存どおり`Transient`とする。
- `Authenticated → Error/状態取得例外 → Authenticated`では配送境界を変更せず、障害中に成立した通知を回復後も配送対象にできる。
- `Authenticated → ReauthenticationRequired → Authenticated`では新しい配送境界を開始し、失効期間の通知を後送しない。

### AC-023 Single instance

- 同じWindowsユーザーでは1つのインスタンスだけがユーザー単位の名前付きMutexを取得できる。
- 2個目は案内を表示して終了し、監視、App Server、トレイ、Gmail、Windows通知、状態・履歴書込みを開始しない。
- 所有インスタンス終了後は次の起動がMutexを取得できる。プロセス異常終了時もOSがハンドルを解放する。
- Windowsユーザーが異なる場合はMutex名が衝突しない。

### AC-024 Status accuracy

- Gmail通知設定の有効・無効と、OAuthの認証済み・未認証・再認証必要・エラーを別々に表示する。
- 認証済みアカウントを表示できるが、トークン、`client_secret`、認証エラー本文に含まれ得る機密値は表示しない。
- WindowsとGmailの最終通知を全体および利用枠ごとに独立表示し、一方だけ成功した場合も「通知なし」と誤表示しない。
- `rateLimitResetCredits.availableCount`を「利用可能リセットクレジット数」と表示し、通常の周期的リセット回数と説明しない。

### AC-025 Windows autostart

- `AutoStartEnabled=true`を保存すると、CurrentUser Runキーへ引用符付きexeパスと固定`--autostart`引数を登録できる。
- `AutoStartEnabled=false`を保存すると本アプリの登録名だけを削除できる。
- 自動起動では状態画面を表示せずトレイへ常駐し、単一インスタンス制御により二重監視を開始しない。
- 設定ON・Registry OFF、設定OFF・Registry ON、別exe登録を不一致として表示し、起動時に設定値へ同期できる。
- OS変更失敗時は設定を保存せず、設定保存失敗時はOS状態をロールバックする。ロールバック失敗は不一致として検出できる。
- `dotnet.exe`と未publishの開発ビルドをRunキーへ登録しない。
- Registry実操作テストは一意なテスト登録名を使い、本番登録を変更せず後始末する。

### AC-026 Usage history retention

- `CapturedAtUtc >= 現在UTC - HistoryRetentionDays`の取得行を保持し、それより古い正常行だけを削除できる。
- 複数利用枠を含む1取得行を分割せず、保持境界ちょうどの行を保持できる。
- 破損行をログへ記録して保持し、原子的置換の失敗またはCancellation時に元履歴を維持できる。
- Appendと保守の並行要求で新しい履歴を失わず、同時保守を直列化できる。
- 保守後は保持履歴からobservedKeysを再構築し、90日以上消えていた組み合わせを再度新規検出できる。

### AC-027 Log retention

- `codex-usage-notifier-yyyyMMdd.log`のうち保持境界より古い対象だけを削除し、削除件数を返せる。
- 当日、前日、保持境界以降、形式違い、不正日付、別名ログを削除しない。
- 1ファイルの削除失敗を件数とログへ記録し、他の保守と監視を停止しない。
- `LogRetentionDays`の7～3650を許容し、範囲外のファイル値を30へ補正できる。

### AC-028 Maintenance reliability

- 初回または前回試行から24時間以上で保守を実行し、24時間未満では履歴・ログを書き換えない。
- 複数トリガーをsingle-flightで処理し、履歴失敗後もログ保守を、ログ失敗後も監視を継続できる。
- `LastMaintenanceAtUtc`をVersion 3→4 migrationで追加し、future state schemaの無変更拒否を維持する。
- UsageMonitor開始を保守完了待ちで遅らせず、アプリ終了Cancellationでバックグラウンド保守を停止できる。

## 11. 単体テスト対象

最低限、次を単体テストする。

1. 残量計算
2. Positionを維持した300分・10080分・Unknownの分類
3. 短期枠回復の99%閾値判定
4. リセット期間ID生成
5. LimitId・Position・WindowDurationMinutes・リセット期間ID・通知種別・通知段階による重複通知防止
6. 起動時通知判定
7. 日付をまたぐ通知禁止時間
8. 保留通知終了時の再取得と再判定
9. 長期枠の48時間・50%以上の早期通知判定
10. 長期枠の24時間・20%以上の通常通知判定
11. 長期枠の6時間・10%以上の最終通知判定
12. 期限切れのリセット前通知の破棄
13. リセット予定後の再取得とリセット完了判定
14. `resetsAt`変化と50ポイント以上の使用率低下、および各判定理由
15. Unknown枠の通知除外、表示、履歴保存
16. 再試行間隔
17. 3回失敗時の障害通知
18. 複数通知状態を含む状態ファイルの読み書き
19. 履歴保持期間の削除
20. 通知種別に応じたGmail本文生成
21. OAuth情報がログへ出ないこと
22. 全limitId・全利用枠の保持
23. FiveHourとWeeklyの同時有効化およびUnknownの既定無効化
24. 取得単位の全利用枠履歴保存
25. 新しいLimitId・Position・WindowDurationMinutesの組み合わせの検出
26. 週間枠だけの実アカウント相当レスポンスの処理
27. `resetsAt`なしの短期枠における閾値遷移、継続中の重複抑止、回復連番の更新
28. `resetsAt`なしの長期枠におけるリセット前通知の抑止
29. 使用率が50ポイント低下した場合と49ポイント低下した場合の境界
30. テスト通知によって本番の通知状態と利用枠履歴が変化しないこと
31. 複数利用枠の通知状態が相互に干渉しないこと
32. 設定画面ViewModelの初期設定読み込み
33. 設定編集、JSON保存、および監視反映先への通知
34. キャンセルによる変更破棄
35. 画面項目の初期値復元
36. 通知閾値1%・100%と範囲外の境界
37. Early、Standard、Finalの正数と時間順序
38. Gmail送信先のメールアドレス形式
39. 日付をまたぐ通知禁止時間の入力
40. 不正なResetInferenceUsageDropPointsだけの初期値フォールバック
41. 設定保存時に通知済み状態と回復連番を維持すること
42. Unknown枠の設定画面上の通知除外
43. Gmail未認証時の有効化抑止
44. 分類別既定通知設定の編集反映
45. 設定した使用率低下推定閾値によるリセット完了判定
46. 同一取得で成立した複数候補のWindows通知集約
47. Windows通知の失敗後再送、成功後抑止、および最大3回制限
48. 古いWindows`InProgress`の再試行可能状態への回復
49. Final無効時とStandard無効時の下位段階へのフォールバック抑止
50. 保留終了前、24時間超過、現在期間不一致の保留除外
51. 連続更新通知のデバウンスによる1回取得
52. Windows無効・Gmail有効時の候補保持
53. Windows成功済み・Gmail未送信時のWindows重複送信抑止
54. WindowsとGmailそれぞれの試行情報のJSON永続化
55. OAuthクライアント設定なしのNotConfigured状態
56. 不正なOAuthクライアント設定の拒否と既存ファイル維持
57. 正常なデスクトップアプリ設定の標準配置
58. OAuth認証成功とキャンセル後の状態
59. OAuth認証の同時実行抑止
60. DPAPIストアの保存・同一ユーザー読み込み・非平文確認
61. 破損・復号失敗した認証情報の再認証案内
62. 認証解除のGoogle側失効結果とローカル削除、およびGmail設定無効化
63. 期限切れアクセストークンの更新と更新時刻保存
64. `invalid_grant`および401の再認証必要状態
65. 一時通信障害時の認証情報維持
66. 認証成功時の空のGmailRecipient初期設定
67. 認証済みかつ有効送信先の場合だけのGmail設定保存・テスト送信
68. MIMEのFrom、To、Subject、本文、CRLF、およびUTF-8日本語
69. Base64URLから`+`、`/`、末尾`=`が除かれること
70. Gmail APIの403、API未有効化、一時サーバーエラー分類
71. Gmailテスト送信の同時実行抑止
72. テスト送信成功・失敗時の本番通知状態、回復連番、履歴非変更
73. Gmailテスト送信によるWindows配送状態非変更
74. トークンとクライアントシークレット相当値がログへ出ないこと
75. Gmail有効・認証情報利用可能・有効送信先での本番通知送信
76. Gmail無効または未認証時の本番送信抑止
77. Windows無効・Gmail有効時のGmail単独配送
78. Windows成功済み・Gmail未送信時のGmail単独配送
79. Gmail成功済み・Windows未送信時のGmail再送抑止
80. 同一取得の複数候補と複数limitIdの1通集約
81. 集約成功時の候補別Gmail成功状態
82. 集約失敗時の候補別Gmail失敗状態とWindows状態維持
83. Gmail一時失敗時の試行回数1、最終試行時刻、および60分後の次回再試行時刻
84. Phase 4C開始時刻より前の保存済みNotAttempted状態の送信抑止
85. Phase 4C開始時刻以降の候補の送信
86. `GmailProductionDeliveryStartedAtUtc`のJSON永続化と再起動後維持
87. 通知禁止時間中のGmail送信抑止
88. 禁止時間終了後に有効な保留候補をGmail送信
89. 時間帯を過ぎたEarly／Standard／FinalのGmail期限切れ
90. `ResetTimeAdvanced`の本文表現
91. `UsageDropInference`の推定表現
92. Gmail成功後の同一候補再送抑止
93. Gmail初回一時失敗後、60分未満では再試行しないこと
94. Gmail本番本文にOAuthトークン名や認証ヘッダーが含まれないこと
95. Gmail本番本文で未使用分の消滅・繰り越しを断定しないこと
96. Phase 4BのMIME生成とGmail APIクライアントを本番送信でも共有すること
97. テストメール成功・失敗による本番Gmail配送状態非変更
98. 401または`invalid_grant`の再認証必要状態
99. 一時通信障害時の認証情報維持
100. 初回一時失敗から60分後の再試行と、2回失敗後の自動再試行抑止
101. Gmail API 429・5xx・タイムアウトの一時障害分類
102. Gmail API 401、`invalid_grant`、恒久403の自動再試行抑止
103. 複数再試行候補の1通集約
104. 新規候補と再試行候補の1通集約、および候補別試行回数
105. EarlyからStandard、StandardからFinalへ進んだ古い再試行の期限切れ
106. 短期回復が閾値未満へ戻った場合の再試行期限切れ
107. 同じ新期間を表すリセット完了の再試行
108. 通知禁止時間中のGmail試行回数維持と、終了後の再試行
109. 60分未満のGmail`InProgress`抑止、60分以上の回復、および最大2回制限
110. Gmail無効期間の通知抑止と、再有効化後の新規通知配送
111. `GmailDeliveryEnabledSinceUtc`とGmail失敗分類のJSON永続化
112. 再認証成功時の配送有効期間境界更新
113. Windows成功・Gmail失敗、Windows失敗・Gmail成功の独立状態
114. Gmail再試行によるWindows状態非変更とWindows再試行によるGmail状態非変更
115. Gmail成功済み候補の再送抑止
116. 現在SchemaVersionの無変更読込、旧SchemaVersionの段階migration
117. future schemaの拒否と、元ファイル内容・更新日時・名前・配置の完全維持
118. future schema時の監視初期化抑止と安全なユーザー案内
119. 破損state JSONの既存復旧動作
120. Gmail権限不足403のAuthentication分類と再認証状態
121. `accessNotConfigured`、`serviceDisabled`、未知の恒久403のPermanent分類
122. 一時認証Error・状態取得例外からの回復時にGmail配送境界を維持すること
123. 明示的な再認証完了時だけGmail配送境界を更新すること
124. 一時認証障害中の通知維持と、認証失効中の通知後送抑止
125. 同じMutex名の初回取得、2個目拒否、解放後の再取得
126. 2個目の起動経路で監視、App Server、状態書込みを開始しないこと
127. Windowsユーザー識別子ごとのMutex名分離
128. Gmail設定、OAuth認証、認証アカウントの状態表示
129. Windows／Gmail別、および利用枠別の最終通知表示
130. 状態画面にトークンと`client_secret`を表示しないこと
131. 自動起動コマンドの引用符、固定引数、CurrentUser限定、および無効化削除
132. `dotnet.exe`と未publish開発出力の自動起動登録拒否
133. 設定ON／OFFとRegistry状態の不一致検出、および設定値への同期
134. 自動起動変更失敗時の設定非保存と、設定保存失敗時のOSロールバック
135. 一意なテスト登録名を使うCurrentUser Runキー統合テスト
136. 90日以内、90日超、保持境界ちょうど、および複数枠取得行の履歴保守
137. 破損履歴行の保持、Cancellation時の元ファイル維持、一時ファイル後始末
138. Appendと履歴保守の並行実行、および同時保守の排他
139. 保守後observedKeys再構築と90日以上消えた枠の再新規検出
140. 30日以内、30日超、当日、前日、保持境界のログ保守
141. 形式違い、不正日付、別名ログの保護と削除失敗の非致命処理
142. HistoryRetentionDays／LogRetentionDaysの7～3650境界と個別フォールバック
143. 初回、24時間未満、24時間経過の運用保守期限判定
144. 複数保守トリガーのsingle-flight
145. 履歴失敗後のログ保守継続、ログ失敗後の非致命動作
146. アプリ終了Cancellationによるバックグラウンド保守停止
147. Version 3→4の`LastMaintenanceAtUtc` migrationとfuture schema保護回帰

## 12. 実装上の設計方針

### 12.1 主要インターフェース案

```text
ICodexRateLimitClient
IUsageMonitor
INotificationPolicy
INotificationSender
IWindowsNotificationSender
IGmailNotificationSender
IGoogleOAuthClientConfigurationService
IGmailAuthenticationService
IGmailAuthenticationStatusProvider
IGmailCredentialStore
IGoogleOAuthFlow
IGmailApiClient
IGoogleGmailMessageGateway
IGmailMimeMessageFactory
IGmailTestMailSender
IUsageHistoryRepository
IUsageHistoryMaintenance
ILogMaintenance
IApplicationMaintenanceService
IApplicationStateRepository
ISettingsRepository
ISettingsChangeSink
IClock
IAutoStartManager
IPowerEventSource
```

### 12.2 依存方向

```text
Presentation
     ↓
Application
     ↓
Domain
     ↑
Infrastructure
```

Domain層は、WPF、Gmail、JSON-RPC、ファイルシステムへ直接依存しない。

### 12.3 時刻

- 内部時刻：UTC
- 表示時刻：Windowsのローカルタイムゾーン
- テスト：`.NET TimeProvider`を差し替えて固定時刻を注入

## 13. 配布方針

初版では次を想定する。

- x64向けWindowsアプリ
- 自己完結型またはフレームワーク依存型のいずれかをビルド時に選定
- インストーラーは後工程
- 初期開発中はVisual Studioまたは`dotnet run`で起動
- OAuthクライアント設定は利用者自身のGoogle Cloudプロジェクトで作成
- シークレットや認証ファイルは配布物・リポジトリに含めない

## 14. 将来機能：利用枠連動バックログ自動実行

### 14.1 目標

Codexの利用枠が回復した際に、承認済みバックログから実行可能な作業を選び、Codexへ自動投入し、作業結果に応じてバックログを更新する。

### 14.2 想定フロー

```text
利用枠回復
   │
   ▼
バックログ取得
   │
   ▼
実行可能性を判定
   │
   ├─ 依存未解決 → スキップ
   ├─ 人間承認必須 → 通知
   └─ 自動実行可
          │
          ▼
Codex実行用ワークスペースを準備
          │
          ▼
作業を実行
          │
          ▼
ビルド・テスト・静的解析
          │
          ├─ 成功
          │    ├─ 差分と結果を保存
          │    ├─ バックログを完了へ更新
          │    └─ 人間へ通知
          │
          └─ 失敗
               ├─ バックログを要確認へ更新
               ├─ 自動実行を停止
               └─ 人間へ通知
```

### 14.3 将来決める必要がある仕様

- バックログの保存先
  - GitHub Issues
  - GitHub Projects
  - `BACKLOG.md`
  - 専用JSON
- 自動実行を許可するラベル
- 対象リポジトリ
- 対象ブランチ
- 1回に実行する最大件数
- 利用枠の最低残量
- 作業時間とトークンの上限
- 許可コマンド
- ネットワークアクセス
- 変更可能なディレクトリ
- コミット・プッシュ・PR作成の可否
- テスト成功の定義
- 完了判定
- 失敗時のロールバック
- 人間の承認ポイント
- 連続失敗時の停止条件
- バックログ更新の競合処理
- 秘密情報へのアクセス制限

### 14.4 初版で確保する拡張点

初版では自動実行しないが、次を分離しておく。

- 通知条件成立イベント
- バックログ連携用のイベント購読点
- 通知処理と後続処理の分離
- 外部ワークフロー実行インターフェース
- 利用枠スナップショットの永続化

## 15. 開発フェーズ

### Phase 1：基盤

- ソリューションとプロジェクト作成
- WPF起動
- タスクトレイ
- DI
- ログ
- 設定・状態保存
- 単体テスト基盤

### Phase 2：Codex連携

- App Server起動
- JSON-RPC初期化
- 利用枠取得
- 画面表示
- エラー表示
- 全limitId・全利用枠の保持と分類
- 利用枠別通知設定の識別モデル
- 全利用枠のJSONL観測履歴保存
- 新規利用枠の検出ログ
- 更新通知を契機としたデバウンス再取得
- 同時要求の集約
- 自動再接続
- 現在のCodex CLIに対応するJSON Schemaの保存

Phase 2では通知判定、通知状態管理、通知段階・リセットまでの残り時間の表示、Windows通知、Gmail通知、履歴グラフを実装しない。観測履歴の保存はPhase 2に含める。

### Phase 3：監視とWindows通知

- 次回リセット監視
- 1時間ごとの補助確認
- 短期枠回復通知判定
- 長期枠の段階的なリセット前通知判定
- 長期枠の再取得確認後のリセット完了通知判定
- 利用枠・リセット期間・通知種別・通知段階による重複防止
- 通知禁止時間中の保留と終了後の再取得・再判定
- スリープ復帰
- Windows通知

Phase 3では、Windows通知を既存のタスクトレイアイコンから表示するバルーン通知として実装する。`usedPercent`によるリセット完了の補助判定は、初期値として50ポイント以上の低下を使用する。

### Phase 3.1：複数枠通知基盤の完成

- LimitId、Position、WindowDurationMinutes単位の通知種類設定
- FiveHourとWeeklyを含む複数利用枠の独立した同時判定
- Classification別の既定通知設定とUnknownの既定除外
- `resetsAt`なしの短期枠に対する永続回復連番
- `resetsAt`なしを含む使用率50ポイント低下によるリセット完了補助判定
- ResetTimeAdvancedとUsageDropInferenceの判定理由保存・表示・ログ
- 枠ごとの通知設定、リセット情報、最終通知、判定理由、回復連番の表示
- 本番状態と履歴を変更しない6種類のWindowsテスト通知

Phase 3.1ではGmail送信とGmail OAuthを実装しない。利用枠別設定の内部モデルとClassification別の既定値を実装し、設定画面からの編集は後続フェーズとする。

### Phase 3.2：通知信頼性改善

- 同一取得で成立した複数候補のWindowsバルーン集約
- Windows配送失敗の5分間隔・最大3回再試行
- 5分以上残った`InProgress`の回復
- Early、Standard、Finalの排他的な時間帯判定
- 保留通知の期間ID、保留終了時刻、24時間の鮮度検証
- 更新通知デバウンスのCTS競合解消
- WindowsとGmailのチャネル別配送状態・試行情報

Phase 3.2ではGmail APIによる送信は行わない。Gmailだけが有効な場合も候補と未送信状態を保持し、Phase 4Cの本番配送実装が引き継げる構造とする。

### Phase 4A：設定画面

- タスクトレイと状態画面から開くWPF設定画面
- 全般、短期枠、長期枠、Gmail表示項目の編集と検証
- FiveHourとWeeklyの分類別既定通知設定
- 取得済み利用枠と適用通知設定の表示
- Unknown枠の表示と設定画面上の通知有効化抑止
- 非同期の設定読み込み・原子的保存
- キャンセル、初期値復元、未保存変更の破棄確認
- 保存後の監視タイマーと状態表示への再起動なし反映
- `ResetInferenceUsageDropPoints`の内部設定化と不正値フォールバック

Phase 4AではGoogle OAuth、Gmail API、Gmail送信、テストメール送信を実装しない。Gmail認証関連ボタンは無効表示とする。自動起動は設定値だけを扱い、Windowsへの登録処理は実装しない。

### Phase 4B：Gmail

- デスクトップアプリ用Google OAuthクライアント設定の検証・標準配置
- システム既定ブラウザー、PKCE、ローカルループバックによるGoogle OAuth
- Gmail認証状態、再認証案内、認証解除
- DPAPI CurrentUserによるスキーマバージョン付き認証情報保護
- Google公式クライアントによるアクセストークン自動更新
- Gmail API `users.messages.send`によるUTF-8テストメール
- 設定画面への認証・解除・テスト送信統合
- 機密情報を出さないログとエラー分類

Phase 4Bでは短期枠回復、長期枠リセット前、長期枠リセット完了の本番通知メールを送らない。テスト送信は本番通知状態と履歴を変更しない。

### Phase 4C-1：Gmail本番配送

- 既存の共通通知候補からチャネル別にGmail未送信分を配送
- Windows無効・Gmail有効を含むチャネル独立動作
- Phase 4C導入前の通知を除外する`GmailProductionDeliveryStartedAtUtc`
- 同じ取得の複数候補と複数limitIdを1通へ集約
- 通知禁止時間中の本番Gmail保留と終了後の再評価
- `GmailDeliveryStatus`・試行情報の候補別更新
- Phase 4BのMIME生成、Base64URL変換、Gmail APIクライアントの共有

Phase 4C-1ではGmailの初回送信だけを実行し、失敗後の自動再試行タイマーは追加しない。

### Phase 4C-2：Gmail配送再試行

- 一時障害だけを対象とする60分後の1回再試行（初回と合わせて最大2回）
- 専用短時間タイマーを使わず、次の正常取得を契機とする`GmailNextRetryAtUtc`評価
- 新規候補と再試行候補を含む1通集約
- 警告段階、短期残量、リセット期間による期限切れ判定
- 通知禁止時間中の試行抑止と終了後の再評価
- 60分以上古いGmail`InProgress`の試行回数を維持した回復
- 認証失効時の再認証案内と、古い認証失敗通知の再送抑止
- `GmailDeliveryEnabledSinceUtc`による無効期間・認証失効期間の後送防止
- Windows／Gmailの独立再試行

Phase 4C-2でも専用の短時間再試行タイマーと、本番アプリで偽の通知候補を生成する機能は追加しない。実Googleアカウントの本番配送は自然な通知条件が成立した際の手動確認項目とし、Phase 4C-2の完了条件には含めない。

### Phase 5前Release Gate：信頼性・互換性・運用安全性

- future `state.json`の無変更拒否と、旧版の明示的な段階migration
- Gmail 403の認証・権限失効、API未有効化、恒久拒否の分類
- 一時認証状態取得エラーからの回復時に配送境界を維持
- Windowsユーザー単位の単一インスタンス制御
- Gmail通知設定、OAuth認証、Windows／Gmail別最終通知の状態表示
- `rateLimitResetCredits.availableCount`を利用可能リセットクレジット数として明確化

このRelease Gateでは、Windows自動起動登録、履歴90日削除、ログ30日削除、配布ビルド、インストーラー、CI、正式アイコンを実装しない。前3項目は後続のPhase 5Aで実装する。

### Phase 5A：常駐運用機能

- CurrentUser RunキーによるWindowsログイン時の自動起動
- 設定値を正とする起動時同期、設定保存時ロールバック、OS登録状態表示
- 開発実行パスの永続登録防止と、自動起動時のトレイ専用表示
- 履歴の90日保持、破損行保持、原子的置換、Appendとの排他、observedKeys再構築
- 対象名へ限定したログの30日保持
- 起動時と24時間ごとのsingle-flight保守、非致命エラー、終了Cancellation
- `ApplicationState` Version 4とVersion 3→4 migration

Phase 5Aではインストーラー、MSIX、MSI、ClickOnce、GitHub Actions、Release自動作成、コード署名、正式アイコン、配布ZIP、自動アップデート、バックログ自動実行を実装しない。

### Phase 5B：配布とCI

- GitHub Actionsによるビルド・テストCI
- Release向けpublish構成
- 配布形式の確定
- 配布パッケージ作成
- 必要に応じたインストーラー、コード署名、正式アイコンの検討

## 16. 未決事項

以下は実装開始後、実機確認を踏まえて決定してよい。

1. Google OAuth同意画面がテスト公開・本番公開の各構成で期待どおり表示されるか
2. アクセストークン自動更新、Google側権限取消、再認証を実アカウントで一巡確認できるか
3. 自分宛てGmailのPC・スマートフォン・タブレット通知の実機挙動
4. 配布形式
5. アプリ名・アイコン
6. 300分枠を返す実アカウントで、5時間枠候補と閾値遷移の実挙動を確認できるか
7. 短期枠回復通知の保留中に残量が閾値未満へ下がった場合も、回復していた事実を通知するか。Phase 4C-2のGmail再試行では期限切れとして送らない
8. Unknown枠で通知を手動有効化する場合、どの通知種類を推奨するか
9. LimitId単位の上書き通知設定を設定画面から直接編集できるようにするか
10. `ResetInferenceUsageDropPoints`を一般ユーザー向け画面へ公開するか
11. 未使用分が次の利用期間へ繰り越されるか。公式レスポンスからは未確認であり、通知文では断定しない
12. Windows再ログイン後の自動起動、トレイ専用表示、単一インスタンス、UsageMonitor開始を配布用exeで一巡確認できるか
13. Phase 5Bで採用するpublish方式、配布形式、コード署名、インストーラーの要否

## 17. 公式参考資料

- OpenAI Codex App Server  
  https://developers.openai.com/codex/app-server
- Gmail API: Create and send email messages  
  https://developers.google.com/workspace/gmail/api/guides/sending
- Google OAuth 2.0 for iOS & Desktop Apps  
  https://developers.google.com/identity/protocols/oauth2/native-app
- Google API Client Library for .NET: OAuth 2.0  
  https://developers.google.com/api-client-library/dotnet/guide/aaa_oauth
- Google Workspace: Create access credentials  
  https://developers.google.com/workspace/guides/create-credentials
