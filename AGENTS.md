# AGENTS.md

このRepositoryでCodexその他のCoding Agentが作業する際の恒久的な作業指示です。

## 1. Source of Truth

作業開始前に必ず以下を確認すること。

1. `README.md` — 製品目的、ユーザー価値、公開版の利用方法
2. `SPEC.md` — 機能、安全要件、Acceptance Criteriaの正式仕様
3. 対象TaskのGitHub Issue / Pull Request — 今回の作業Scope、追加制約、完了条件
4. 関連する`docs/`配下の文書 — Gmail OAuthなど対象機能の利用者向け仕様
5. `.github/workflows/`および`eng/` — CI / Release / License / Vulnerability Gate

仕様が矛盾する場合は、製品仕様については`SPEC.md`を優先すること。
Task固有のIssue / PRは`SPEC.md`を勝手に上書きする根拠にせず、矛盾を発見した場合は作業を止めて報告すること。

Codex App ServerやGoogle OAuthなど外部サービスの挙動を推測で仕様化しないこと。
現在取得できない情報は「未観測」「不明」として扱い、もっともらしい値で補完しないこと。

未確定事項や将来機能を、明示指示なしに先回り実装しないこと。

## 2. Required Git Workflow

実装作業をdefault branchへ直接行わない。

- 作業開始時に最新のdefault branchを基準にする
- Task用Branch / Worktreeが既に提供されている場合はそれを使用する
- 提供されていない場合は`codex/<short-task-name>`形式のBranchを作成する
- 1つの大きなCommitにまとめず、仕様上意味のある単位ごとにCommitする
- 既存Commitをamend / rewriteしない（明示指示がある場合を除く）
- 各Commit前に関連Testを実行する
- 作業終了前にFull Build / Testを実行する
- RemoteへPushし、原則としてDraft Pull Requestを作成する
- PR本文に変更内容、理由、影響、検証結果、残課題を記載する
- CIが失敗している状態を完了扱いしない。修正不能なら原因をPRへ明記する
- Releaseや公開物に影響する変更では、通常CIだけでなくRelease Gateも確認する

## 3. Architecture and Implementation Discipline

### 3.1 Layer responsibility

現在の責務境界を維持すること。

- `Domain`
  - 利用枠、通知種別、設定、状態などの純粋なモデルと判定ロジック
  - WPF、Windows API、Google API、JSON-RPC wire format、ファイルI/Oへ依存させない
- `Application`
  - Use Case、Service、Abstraction、処理のオーケストレーション
  - Infrastructureの具体実装へ直接依存させない
- `Infrastructure`
  - Codex App Server、永続化、Windows、Gmail/OAuth、DPAPI、Registry、ファイルシステム等の外部I/O
- `Presentation`
  - WPF UI、ViewModel、表示用Formatter / Control
  - 通知成立条件などのDomain Logicを実装しない

UI都合でDomain ModelへPresentation専用情報を追加しないこと。
Presentationでは完成済みの表示文字列を再解析して状態判定しないこと。必要な値は構造化されたProperty / ViewModelとして公開すること。

### 3.2 Scope

- `SPEC.md`とTaskのAcceptance Criteriaを超えて無関係な機能を先回り実装しない
- Bug Fixでは、問題の再現条件を最小化し、原因箇所以外への変更を広げない
- RefactorはTask達成に必要な範囲へ限定する
- 新しいFramework、DI Container、MVVM Framework、Chart Library等を安易に追加しない
- Long-running処理はCancellation、安全なShutdown、例外境界を考慮する
- Partial Failureでは、取得済みの安全な情報を不必要に失わない
- UIの失敗や履歴読込失敗だけで監視・通知Service全体を停止させない

## 4. Codex App Server and Rate-Limit Rules

### 4.1 Process ownership

- 本アプリが起動した`codex app-server`子プロセスだけを管理する
- 他のCodex / Codex CLIプロセスを検索、再利用、終了しない
- WindowsApps配下の実体をコピーして起動しない
- 正常終了を試みた後、必要な場合だけ本アプリ所有のプロセスツリーを終了する
- Timeout / Shutdown / pending requestにはCancellationを考慮する

### 4.2 JSON-RPC / acquisition

- stdin/stdoutはApp ServerとのJSONL通信として扱う
- App Serverのwire formatをPresentationやDomainへ漏らさない
- 不正JSON、未知通知、未知fieldだけでアプリ全体を終了しない
- `account/rateLimits/updated`だけで利用枠状態を確定せず、正式なread結果を取得してから状態を更新する
- 同時取得要求を無制限に積まず、既存のsingle-flight / coalescing方針を維持する
- stderrや診断Logへ認証情報、生JSON、機密値を無加工で出力しない

### 4.3 Rate-limit interpretation

- `primary` / `secondary`はPositionであり、利用枠の意味として扱わない
- `windowDurationMins == 300`をFiveHour候補とする
- `windowDurationMins == 10080`をWeekly候補とする
- その他はUnknownとして保持する
- 5時間枠が存在しない状態を正常な取得結果として扱う
- Unknownを推測でFiveHour / Weeklyへ分類しない
- App Serverから取得できない`resetsAt`等を推定値で埋めない
- Reset Creditを通常の周期的リセット回数として扱わない
- ObservationとInferenceを明確に分離する

## 5. Notification Safety

通知は単なるUI機能ではなく永続状態を伴うため、変更時は重複・誤通知を最優先で防ぐこと。

### 5.1 Short-window recovery

- 初回観測が高残量であるだけでは回復通知しない
- 回復通知には、同一利用枠で回復前の低下を観測済みであることを要求する既存仕様を維持する
- 同一回復Sequenceで重複通知しない
- FiveHourが未観測でもエラー扱いしない

### 5.2 Long-window warnings / reset

- Early / Standard / Finalの時間帯を重複させない
- `resetsAt`がない場合にリセット前残り時間を推測しない
- Timerが予定時刻へ到達しただけでリセット完了を確定しない
- リセット完了は既存仕様どおり、確認可能な期間更新または明示されたInference条件に基づく
- `ResetTimeAdvanced`と`UsageDropInference`の意味を混同しない

### 5.3 Dedupe / deferred / retry

以下の既存境界を壊さないこと。

- 利用枠ごとの複合キーによる独立状態
- 同一期間・同一通知種別・同一段階の重複防止
- Quiet Hours中の送信保留
- Quiet Hours終了後の再取得・再判定
- stale deferred notificationの期限切れ
- WindowsとGmailの配送状態の独立
- 成功済みChannelへの再送禁止
- 一時障害だけを対象とする限定Retry
- 再起動後の重複防止
- Test Notificationが本番通知Stateを変更しないこと

通知ロジック変更では正常系だけでなく、再起動、Quiet Hours、Retry、部分成功、重複取得を必ずTestすること。

## 6. Gmail / OAuth / Privacy

Gmail通知はOptional機能であり、Google設定なしでもWindows通知だけで全体機能を利用可能な状態を維持すること。

- Gmail未設定をApplication Errorとして扱わない
- 現在の公開版はBring Your Own OAuth Client方式である
- OAuthはシステム既定Browser、loopback redirect、PKCEの既存方式を維持する
- Gmailの受信メールを読むScopeを追加しない
- 現在のScopeを変更する場合は`SPEC.md`、利用者向け文書、Google審査影響を確認する
- Token / credentialはDPAPI CurrentUserで保護し、平文保存しない
- Access Token、Refresh Token、Client Secret等をLog、Exception Message、UI、Test fixture、Release Artifactへ出力しない
- OAuth認証情報を開発者Serverへ送信しない
- Gmail認証失効と一時的な送信障害を混同しない
- Gmail無効化や再認証境界より前の古い通知を後送しない
- MainWindow等の概要表示ではGoogleアカウントを必要に応じてMaskする

OAuth/Gmail仕様を変更した場合は`docs/gmail-oauth-setup.md`も同じPRで更新すること。

## 7. Persistence and Schema Safety

永続化互換性を壊さないこと。

- `settings.json`、`state.json`、`usage-history.jsonl`のSchema変更は明示的な仕様変更として扱う
- UI文言変更や内部Renameだけを理由にSchema Versionを上げない
- State migrationは段階的かつ後方互換を考慮する
- 現在のApplicationが理解できない将来VersionのStateを破壊的に上書きしない
- 永続化途中のCrashで既存の正常データを失わないよう、安全な書換え方式を維持する
- Usage Historyは既存JSONL形式と90日Retentionを維持する（明示仕様変更を除く）
- Logは既存の30日Retentionと対象File Patternを維持する
- 履歴の破損行があっても、保守処理で不必要にデータを消失させない
- Read / Append / Maintenanceが競合する場合は既存の同期境界と整合させる
- 履歴可視化等のために同じ情報を別形式へ重複保存しない（明示指示がある場合を除く）

ユーザー固有の以下のFileはSource RepositoryやRelease Artifactへ含めないこと。

- `settings.json`
- `state.json`
- `usage-history.jsonl`
- `google-oauth-client.json`
- `google-oauth-credentials.dat`
- `*.log`
- その他Token / Credential / User-specific data

## 8. WPF / UI

- Public UIへ`Phase 4B`等の内部開発Phase名を表示しない
- Domain enumや内部State名をそのまま一般ユーザーへ露出しない
- `FiveHour / Weekly / Unknown`等は必要に応じ利用者向け表示へ変換する
- GmailがOptionalであることをUI上でも理解できる状態を維持する
- 色だけで状態を表現せず、Text Labelも併記する
- High DPI、TextWrapping、Keyboard操作、Window Resizeを考慮する
- WPF UI更新はUI Thread境界を守る
- 大量のVisual Element、Timer、Animationを毎更新時に生成しない
- Dashboardの表示用Controlへ監視・通知Business Logicを持たせない
- 外部Icon / UI Libraryを追加する場合はLicenseとRelease Artifactへの影響を確認する
- 実Googleアカウント、Token、Client ID等をScreenshot/Test Dataへ固定しない

## 9. Dependencies, License, and Release Safety

本Project本体はMIT Licenseで公開する。

- 新規NuGet依存を追加する前にLicense、保守状況、必要性を確認する
- Direct / Transitive / Runtime dependencyのLicense auditを壊さない
- `THIRD-PARTY-NOTICES.txt`と`eng/licenses-audit.json`の整合を維持する
- Unknown LicenseやReview Required Licenseを未確認のままRelease可能と判断しない
- 強いCopyleft等、配布条件に影響する依存をユーザー確認なしに導入しない
- `packages.lock.json`を維持し、CI / Releaseではlocked restoreを使用する
- 既知脆弱性Checkを迂回しない
- GitHub Actionsは最小権限を維持し、不要なThird-party Actionを追加しない
- Actionを追加・更新する場合は原則Full Commit SHAでPinする
- `pull_request_target`等、不要に強い権限を伴うWorkflowを追加しない

現在のWindows配布方針を、明示指示なしに変更しない。

```text
win-x64
self-contained
non-trimmed
non-single-file
```

Release Artifactでは、少なくとも以下を維持する。

- Release Versionの整合確認
- `LICENSE`
- `THIRD-PARTY-NOTICES.txt`
- .NET Runtimeの必要なLicense / Notice
- Source / PDB / User Data / OAuth Secretの混入防止
- ZIP生成後の内容再検証
- SHA-256生成・再検証

## 10. C# Documentation

新規・変更するC#コードでは、クラス、interface、record、method、propertyに用途が分かるコメントを付ける。

原則としてXML Documentation Comment（`/// <summary>`）を使用する。

単純な処理内容の言い換えではなく、以下が分かる記述を優先する。

- 責務
- 入出力の意味
- 単位
- null / 未観測の意味
- 前提
- 副作用
- Thread / Cancellation境界
- Security上重要な制約

Private helperも、意図が自明でないものには適切なコメントを付ける。

## 11. Tests

- Bug Fixには必ず再現Testを追加する
- Pure Logicは外部Serviceや実AccountなしでUnit Test可能にする
- App Server AdapterはFake / Test Doubleで検証し、通常CIで実Codex Accountを要求しない
- Gmail/OAuth Testは実Google Accountを通常CIで要求しない
- Windows固有処理はAbstractionを用意し、可能な範囲をUnit Testする
- 実機Testは通常CIから分離する
- Time依存Logicでは固定Clock / Injected Clock等を使い、Testを非決定的にしない
- Notification Logicでは初回観測、重複、再起動、Quiet Hours、Retry、期限切れ、Partial SuccessをTestする
- Persistence変更では旧Schema、Migration、Future Schema、破損Data、AtomicityをTestする
- History変更ではAppend / Read / Maintenance競合とRetentionをTestする
- UI Presentation Logicは実WPF Windowを必要としない範囲をViewModel / Pure HelperでTestする

通常の完了確認として最低限以下を実行する。

```powershell
dotnet restore CodexUsageNotifier.sln --locked-mode
dotnet build CodexUsageNotifier.sln -c Release --no-restore -warnaserror
dotnet test CodexUsageNotifier.sln -c Release --no-build
```

Release / Dependency / Distributionに関係する変更では追加で確認する。

```powershell
./eng/Audit-Licenses.ps1
./eng/Test-NuGetVulnerabilities.ps1
```

必要に応じてwin-x64向けlocked restore、publish、`New-ReleaseArtifact.ps1`まで実行し、Release Artifactを検証する。

Test失敗を既存不具合として無視しない。Task開始時点から失敗していた場合は、再現結果と今回変更との関係を明記する。

## 12. Documentation

仕様、通知意味、Architecture、OAuth、永続化、Release、利用方法が変わる場合は、Codeだけでなく該当Documentも同じPRで更新する。

- 製品概要・利用方法: `README.md`
- 正式な機能・安全仕様: `SPEC.md`
- Gmail OAuth手順: `docs/gmail-oauth-setup.md`
- Workflow / Release変更: 必要に応じREADME / SPEC / Workflow内Comment

新しい重要な設計判断をCode内だけに残さない。

利用者向けDocumentでは内部Phase名や実装詳細を前提にせず、利用者が理解できる言葉で記載する。

## 13. Completion Checklist

Task完了前に確認する。

- [ ] Source of Truthと対象Issue / PRを確認した
- [ ] Scope外の変更がない
- [ ] Domain / Application / Infrastructure / Presentationの責務境界を維持した
- [ ] 通知の重複防止・Quiet Hours・Retry境界を壊していない
- [ ] OAuth / Token / User DataをLogやArtifactへ漏らしていない
- [ ] State / Settings / History Schemaを意図せず変更していない
- [ ] Build成功（warning 0 / error 0）
- [ ] Unit Test成功
- [ ] 必要なRegression Test追加済み
- [ ] 必要なLicense / Vulnerability Check成功
- [ ] 必要なRelease Artifact検証済み
- [ ] Documentation更新済み
- [ ] `git status`が意図した状態
- [ ] 意味のある単位でCommit済み
- [ ] RemoteへPush済み
- [ ] Draft PR作成済み
- [ ] PR本文に変更内容、理由、影響、Validation結果、残課題を記載済み
- [ ] CIが成功している
