# 設定・API・復旧

## 設定

`src/JobRunner/appsettings.json`の`JobRunner`節を使用します。ASP.NET Coreの構成規則に従い環境変数の`__`で階層を指定します。環境変数がファイルより優先されます。配列の部分上書きは末尾要素が残るため、`GET /api/jobrunner/configuration`で実効一覧を確認してください。接続文字列はこのAPIへ含めません。

| キー | 初期値・契約 |
| --- | --- |
| ConnectionString | `UseDevelopmentStorage=true`。本番は接続文字列を外部設定 |
| ManagementContainer | `jobrunner-management`。全入力コンテナーと分離必須 |
| TableName | `JobRunner` |
| SchedulerId | `default`。同じ排他と状態を共有する全インスタンスで一致させる |
| DefinitionSources | 空。1件以上必須。Kind、TargetPath、BlobPath、Encoding |
| DefinitionSources[].Kind | `Unit`のみ。カレンダー属性を持つユニット定義もUnitとして入力 |
| DefinitionSources[].TargetPath | 最上位ユニットの親のJP1完全名。ファイルシステムパスではない |
| DefinitionSources[].BlobPath | `container/blob`。大小文字を保持。ワイルドカードなし |
| DefinitionSources[].Encoding | `utf-8`。コードページ名の明示指定も可能。変換不能バイトは拒否 |
| ScheduleTimeMode | 24。48では47:59までの時刻を日付を失わず保持 |
| TimeZone | `Asia/Tokyo`。夏時間の曖昧・欠落時刻は拒否 |
| ConfirmedHorizonDays | 32。1〜366日 |
| MaxConcurrency | 4。1〜128プロセス |
| LocalAgents | 空。ex属性をローカルへ対応付ける名前の許可一覧 |

Blobの内容変更、配置先・入力一覧変更、時間制等の設定変更は新しい採用IDに分離します。同内容への切り戻しも別採用です。同じ入力・設定の通常再起動では採用ID、登録、保留、変更済み予定、履歴を維持します。入力のETagだけが変わっても再登録しません。文字コードは解釈を変えるため定義ハッシュへ含めます。

## 管理API

ここに記載するのはJobRunner独自APIです。JP1互換HTTP APIは未実装です。

| API | 内容 |
| --- | --- |
| GET `/health/live` | プロセス稼働 |
| GET `/health/ready` | 現時点のBlob/Table到達確認。スケジューラーReadyとは別 |
| GET `/api/jobrunner/status` | IsLeader、Epoch、Phase、Namespace |
| GET `/api/jobrunner/configuration` | 秘密接続設定を除く実効入力一覧 |
| GET `/api/jobrunner/compatibility` | 未対応分類と実機未比較の表示 |
| POST `/api/jobrunner/definitions/validate` | 原文を解析・検証。登録しない |
| POST `/api/jobrunner/definitions/import` | 検証済み不変版の保存。登録しない |
| POST `/api/jobrunner/definitions/{version}/activate` | 採用要求。本文には操作Idを指定 |
| POST `/api/jobrunner/schedules/preview` | 保存済み版、unitPath、anchor、from、untilで予定照会 |
| POST `/api/jobrunner/operations` | 状態変更の受付。初期化前は503 |
| GET `/api/jobrunner/operations/{id}` | Pending/Completed/Rejectedと結果 |
| GET `/api/jobrunner/registrations` | 全採用領域の登録 |
| GET `/api/jobrunner/executions` | 全採用領域の世代 |
| GET `/api/jobrunner/executions/{id}/attempts` | 再実行を含む試行履歴 |
| GET `/api/jobrunner/executions/{id}/logs` | 試行・出力別の分割ログパス一覧 |
| GET `/api/jobrunner/logs?path=...` | 個別ログチャンク |
| POST `/api/jobrunner/executions/{id}/resolve` | 結果不明の明示解決要求 |

検証・インポートの本文は次の配列です。base64は**原文バイト列**で、文字コードの自動推定はしません。

```json
[
  {
    "mapping": {
      "kind": "Unit",
      "targetPath": "/業務",
      "blobPath": "manual/jobs.txt",
      "encoding": "utf-8"
    },
    "base64": "dW5pdD1uO3t0eT1uO30="
  }
]
```

操作例（日時は実行したいUTCへ置き換える）:

```json
{
  "id": "business-run-20261105-01",
  "kind": "Register",
  "target": "/業務/日次/daily",
  "registrationKind": "Immediate",
  "due": "2026-11-05T03:00:00Z"
}
```

即時登録にもDueを必須とし、再処理で日時を変えません。初期化基準より前の予定はスキップします。Confirmedは固定定義から先読み補充します。Unregisterは対象登録IDを指定し、未開始ルートを止め、解除済み行を残します。

世代IDをtargetとする操作はHold、Release、Move（due必須）、Restore、Cancel、Stop、Rerun、Resolveです。状態が適合しない操作はRejectedとなります。保留解除や再実行では先行・親の状態も開始条件になります。Stop後の副作用を判定できない場合はUnknownとします。Rerunは新しいattemptIdを発行し、以前の試行を保存します。

要求Idは1〜128文字。同じId・同じ内容は保存済み結果を返し、異なる内容は409です。新規受付は202と照会URLを返します。変更は稼働系で行います。受付時の採用IDをサーバーが固定し、処理前に版が変わった要求を別版へ誤適用しません。HTTPタイムアウトだけで未実行とは判断せず、同じIdで照会してください。

Unknownの解決には`id`、`expectedState: "Unknown"`、`attemptId`、`reason`、`resolution: "Succeeded"`または`"Failed"`を指定します。元の試行結果を上書きせず、操作行と世代に解決の根拠を残します。

## 排他と障害時の運用

リース期間60秒、更新間隔15秒、安全なローカル期限40秒、待機系の再試行5〜10秒です。リース期限には単調時計を使います。Table更新はleader行の所有者・Epochを照合し、そのETagと対象行を同じトランザクションへ含めます。起動前はStartingと試行を保存し、権限確認後にOSプロセスを起動します。

Blob/TableとOSの起動は一括トランザクションではありません。リース喪失時はプロセスツリー停止を試みますが、実行済みの外部更新は取り消せません。業務側で`JOBRUNNER_EXECUTION_ID`等による冪等性や排他を設けてください。試行ごとの`JOBRUNNER_ATTEMPT_ID`も環境変数で渡します。

復旧手順:

1. `/status`とアプリログで入力検証失敗、Storage障害、状態欠損を区別する。
2. Storageの接続と入力一式の配布を復旧する。入力は更新完了後に起動し、厳密な配布版には不変のリリース別Blobパスを使う。
3. Table有効版を基準に管理Blobを照合する。破損JSONや欠損した登録・子世代を空状態で置き換えない。バックアップを用いて一式を復元する。
4. Unknownの試行について旧ホストと外部業務の結果を確認する。結果を確認せず再実行しない。
5. 根拠を記したResolve操作を送信し、後続の再開と操作結果を確認する。

同じ定義と設定だけでなく、実行ファイル、作業ディレクトリ、OSユーザー環境を各ホストへ配備してください。ローカル生成ファイルを別ホストへ引き継ぐ機構はありません。ログは8Ki文字ごとのチャンクで保存し、完全保存はattemptのLogsCompleteで確認します。履歴・ログの自動保持期限処理は未実装です。
