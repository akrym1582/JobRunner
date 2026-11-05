---
name: jobrunner-verification
description: JobRunnerの変更を試験するとき、Azuriteや実プロセス試験が失敗したとき、PRのCIエラーを調査するときに使う。環境分離、変更に応じた回帰試験、TRXの確認、タイムアウト切り分け、最新コミットのCI確認を行う。
---

# JobRunnerの検証を再現可能にする

以下のパスはリポジトリルートからの相対パスとして読む。このskill自体はコミット・プッシュ・PR作成・マージの新たな権限を与えない。ユーザーが依頼した範囲で実行する。

## 1. 現在の環境と対象を固定する

- `git status --short`、現在のブランチ、HEAD、レビュー基準を確認し、他の変更を保護する。PRを扱う場合は最新ヘッドとローカルとの差を確認する。
- `AGENTS.md`、`global.json`、csproj、`scripts/verify.sh`、`.github/workflows/verify.yml`を読む。SDK・Azuriteの版・検証コマンドは現在のファイルから決め、過去の作業ディレクトリやテスト件数をコピーしない。
- 実HTTPテストの`AppProcess`がどの構成のバイナリを起動するか確認する。現行はRelease。古いバイナリで変更後のテストだけを実行しない。
- 同じチェックアウトのbin/obj/TRXへ同時にbuild/testを走らせない。並行作業が必要なら出力とStorageを分離する。

## 2. テスト用Storageを分離する

- 空いている既定ポートを使える場合は`./scripts/verify.sh`で、新しいAzurite・restore・Release build/test・後片付けまで実行する。
- 既定ポートが使用中なら所有者を確認する。他のサービスを停止しない。自分専用のデータディレクトリと空きポートでAzuriteを起動し、Blob/Tableの疎通を確認する。
- 専用ポートを使う場合は、`StorageScope`が読む`JOBRUNNER_TEST_CONNECTION_STRING`に、そのAzuriteのBlob/Queue/Tableのエンドポイントと開発用アカウントを設定してdotnetを実行する。実Azureや業務用接続先をテストに流用しない。
- `verify.sh`は既定ポートにAzuriteを起動する。専用接続文字列を設定したまま無条件でこのスクリプトを重ねて起動せず、restore/build/testを順に実行する。通常のスクリプト検証では古い接続文字列の上書きを残さない。
- 起動したPIDとディレクトリを記録し、自分が作ったプロセスと資産だけを終了・削除する。接続文字列や認証情報をログへ出力しない。

## 3. 変更に応じた試験を選ぶ

| 変更 | 主な確認先 |
| --- | --- |
| JP1 HTTPの引数・参照・応答 | Jp1ContractTests、Jp1ReadAndScheduleTests、実HTTPと操作後の永続状態 |
| 判定・リカバリー・出力 | CompatibilityExtensionTests、BranchAndIntervalTests、ExecutionTests、PcJobTests |
| 予定・解除・再実行・ネスト | ExecutionControlTests、NetworkOperationTests、SelectiveRerunTests、NestedScheduleTests |
| 生成・復旧・保存形式 | ConfirmedGenerationTests、ReviewRegressionTests、OperationMutationTests、RecoveryTests、DefinitionVersionTests、RetentionTests |
| 稼働系切替・実プロセス・負荷 | IntegrationTests、ScaleTests。規模と測定条件を結果に添える |

テスト名は検索で確認し、変更に直接関係する試験を先に実行する。本体・永続化・APIを変えたら既定の全体検証へ進む。文書やskillだけの修正では構造・リンク・記述の検証を行い、無関係な回帰テストを新設しない。

専用Azuriteへの接続を準備した場合の例（リポジトリルートで実行）:

```bash
dotnet restore JobRunner.slnx
dotnet build JobRunner.slnx -c Release --no-restore
dotnet test JobRunner.slnx -c Release --no-build --filter 'FullyQualifiedName~OperationMutationTests' --logger 'trx;LogFileName=focused.trx'
dotnet test JobRunner.slnx -c Release --no-build --logger 'trx;LogFileName=verification.trx'
```

変更箇所に合わせてfilterを置き換える。終了コードだけでなくTRXの検出・実行・失敗・スキップ件数を確認し、0件検出を合格にしない。ビルドの解析を無効化して通さない。

## 4. 失敗を分類し、根拠を残す

- 最初に失敗したテスト名、例外、スタック、実行条件、対象SHAを保存する。再実行で同じTRXを上書きするなら、失敗の証拠を先に別名で保持する。
- アサーション不一致、起動失敗、Storage応答タイムアウト、HTTP待機タイムアウト、リース喪失を区別する。疎通と最初の失敗箇所を調べ、共有環境が原因と即断しない。
- タイムアウトなら同じテストを単独・分離した環境で確認し、再現条件を比較する。必要なら小さい再現に絞る。成功するまで無根拠に繰り返さず、時間制限・期待値・検出件数を緩めて合格にしない。
- 修正後は失敗ケースと影響範囲を再検証する。新しい変更や未解決の懸念がなければ同じ全体試験を繰り返さない。

## 5. 対象コミットのCIと報告を一致させる

- プッシュが依頼範囲なら、送ったSHAとリモートヘッドを確認する。PR作成・更新は依頼された単位で行い、過去のCI成功を最新コミットの結果と混同しない。
- CIのrun/job/検証ステップとログを確認する。pendingは成功と報告しない。失敗時はログを取得して原因から修正し、再度最新SHAを確認する。
- 結果にはSHA、環境、コマンド、件数、CIリンク、失敗と再試験の経緯、未検証の範囲を記録する。実JP1、Windows、実Azureの検証有無を実施した内容どおりに記す。
- `docs/verification-results.md`の過去結果を無条件に最新扱いせず、今回の記録かPR説明を更新する。マージはユーザーの依頼範囲を別途確認する。
