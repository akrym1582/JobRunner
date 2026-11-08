# 検証結果

## 確認済みの基準

2026-11-07の `main` [a10bb26](https://github.com/akrym1582/JobRunner/commit/a10bb266389a8c3704a59fd6e33bb354cf548a6e)（PR #13マージ後）の[GitHub Actions](https://github.com/akrym1582/JobRunner/actions/runs/42378039235)は成功しました。Ubuntu、.NET 10、Node.js 22、Azurite 3.35.0、Chromiumで検証しました。

| 検証 | 結果 |
| --- | --- |
| Releaseビルド・StyleCop・xUnit | 688件成功、失敗・スキップ0 |
| OpenAPI・aspida | 再生成後の差分なし |
| フロントエンド | lint・format・build成功、Vitest 80件成功 |
| 実APIとAzuriteを使用するブラウザー試験 | Playwright 88件成功 |

再現手順は[README](../README.md#バックエンドのビルドとテスト)と[CI設定](../.github/workflows/verify.yml)を参照してください。CIの `test-results` artifactにはTRX、負荷測定、Playwrightの結果が含まれます。

## 受入シナリオ

| 領域 | 確認した内容・根拠 |
| --- | --- |
| 初回起動・定義反映 | 定義未登録・停止中から明示反映、同一採用IDでのリビジョン更新、旧世代保護、反映中断からの復旧。DefinitionLifecycleTests、PublicationRecoveryTests |
| 再起動・停止制御 | 2プロセスのリーダー交代、旧Epochの書込拒否、Unknownの再実行抑止、全体・グループの停止と再開。IntegrationTests、StorageTests、GroupRecoveryTests |
| 予定計算と生成 | 24/48時間制の対応範囲、業務日・カレンダー・起算・振替、ネストの上位依存と独自設定。子・孫の試算を実際の永続世代と比較。CalendarBoundaryTests、SimpleScheduleRuntimeTests、NestedSchedulePreviewRuntimeTests |
| 実行登録・操作・履歴 | 確定・即時登録、日時変更・取消・解除、保留・再実行、要求の再送と競合、履歴退避とログ参照。FixedRegistrationTests、Jp1ContractTests、OperationMutationTests、RetentionTests |
| 現行の保存形式 | 形式8での初回反映と再起動、旧形式の拒否。導入条件は[運用手順](operations.md#保存形式の更新と切り戻し)を参照 |
| 編集・監視UI | 原文・文字コード保持、移動と複製、予定試算、保存・再取込、古い応答や別タブの競合、実行図・ログ・月間予定。各機能の記録は下記を参照 |
| 集中負荷 | Azurite上で単一ネットの短い実コマンド1,000件を最大32並列で一度ずつ実行。開始遅延、Storage SDK要求数、管理ヒープを `scale.json` に保存。ScaleTests |

機能別の記録は[Web UI](spec/scheduler/web-ui/verification.md)、[定義運用](spec/scheduler/definition-lifecycle/verification.md)、[グループ運用](spec/scheduler/group-operations/verification.md)、[選択移動](spec/scheduler/editor-moves/verification.md)を参照してください。各記録の件数は、記載されたコミットと実行条件に対応します。

## 残る確認と対象範囲

| 区分 | 内容 |
| --- | --- |
| 未検証 | 実JP1の定義・予定・状態・API応答との比較、Windows実機、実Azure。実Azureはローカル・CIの受入に必須ではありません |
| 未検証 | 片側だけStorageに到達できない通信分断やプロセス長時間停止の障害注入、分散した通常負荷と大量ログ・定期退避が重なる条件での計測 |
| 未対応・制限 | 別形式Schedule/Calendar、入力絶対名、一部のカレンダー境界やAPI引数など。詳細は[V12対応表](compatibility-v12.md)と[Web API照合表](jp1-webapi-matrix.md)を参照 |
| 対象外 | 計画実行登録、待ち合わせ・イベント起動、リモートAgent、OSユーザー切替、形式1〜7の運用Storage移行 |

実JP1と比較する場合は、対象V12リビジョンと匿名化したUnit・カレンダー出力、登録基準日と予定一覧、対象APIの要求・応答を揃えます。
