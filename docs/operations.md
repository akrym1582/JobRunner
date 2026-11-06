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
| ConfirmedHorizonDays | 32。1〜366日。未来世代数を指定しない場合の先読み日数 |
| ConfirmedFutureGenerations | 未指定。自動確定登録で維持する未来世代数を1〜99で指定 |
| MaxConcurrency | 4。1〜128プロセス |
| LocalAgents | 空。ex属性をローカルへ対応付ける名前の許可一覧 |
| LocalUsers | 空。所有者実行指定を現在のOSユーザーへ対応付けるJP1ユーザー名の許可一覧 |
| Jp1ManagerName | `localhost`。JP1形式APIで受理するmanager |
| HistoryArchiveAfterDays | 未指定。完了ルート一式をBlobへ退避する正の保持日数 |
| LogRetentionDays | 未指定。結果が確定した試行ログを削除する正の保持日数 |
| OperationArchiveAfterDays | 未指定。完了・拒否した操作本文をBlobへ退避する正の保持日数 |

Blobの内容変更、配置先・入力一覧変更、時間制等の設定変更は新しい採用IDに分離します。同内容への切り戻しも別採用です。同じ入力・設定の通常再起動では採用ID、登録、保留、変更済み予定、履歴を維持します。入力のETagだけが変わっても再登録しません。文字コードは解釈を変えるため定義ハッシュへ含めます。

## 管理API

ここに記載するのはJobRunner独自APIです。JP1形式の対応操作は後述します。

| API | 内容 |
| --- | --- |
| GET `/health/live` | プロセス稼働 |
| GET `/health/ready` | 現時点のBlob/Table到達確認。スケジューラーReadyとは別 |
| GET `/api/jobrunner/status` | IsLeader、Epoch、Phase、Namespace、プロセス内のStorage SDK送信回数RequestCount |
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
| GET `/api/jobrunner/executions/{id}/archive` | 退避済みルートの元の世代・試行JSONとETag |
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

世代IDをtargetとする操作はHold、Release、Move（DueまたはRelativeMinutes必須）、RunNow、Restore、Cancel、Stop、Rerun、Resolveです。状態が適合しない操作はRejectedとなります。保留解除や再実行では先行・親の状態も開始条件になります。Stop後の副作用を判定できない場合はUnknownとします。RerunはFailed/TimedOutの末端ジョブに新しいattemptIdを発行し、以前の試行を保存します。失敗した祖先ネットをRunningへ戻しますが、完了した兄弟は再実行しません。ネットの一括再実行は `rerunMethod: "TOP"` を明示すると全子孫を先頭から再実行します。成功したネットも対象ですが、結果不明・実行中の子孫がある場合は拒否します。選択再実行方式とHOLD/WARNINGも対応します。対応範囲は互換表を参照してください。

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

同じ定義と設定だけでなく、実行ファイル、作業ディレクトリ、OSユーザー環境を各ホストへ配備してください。ローカル生成ファイルを別ホストへ引き継ぐ機構はありません。ログは8Ki文字ごとのチャンクで保存し、完全保存はattemptのLogsCompleteで確認します。保持期限の設定と退避方法は後述します。

## JP1形式の即時登録・保留変更

以下のPOSTを提供します。ユニット完全名はURLエンコードします。

- `/ajs/api/v1/objects/definitions/{unit}/actions/registerImmediateExec/invoke`
- `/ajs/api/v1/objects/statuses/{unit:execID}/actions/changeHold/invoke`

即時登録の例:

```json
{"parameters":{"manager":"localhost","serviceName":"default","holding":true}}
```

保留変更では同じmanager/serviceNameに `"holdAttr":"SET"` または `"holdAttr":"RELEASE"` を指定します。serviceNameはSchedulerIdに一致させます。即時登録の成功は200と `{"execID":"@A0001"}` 形式、保留変更の成功は200と空本文です。execIDはSchedulerId内で永続化された独自の単調採番で、JP1内部の採番順序を保証しません。

任意の `X-JobRunner-Request-Id`（最大100文字）で同じ要求の再送を識別できます。異内容は409、同内容は同じ結果です。応答の `X-JobRunner-Operation-Id` は独自管理APIの照会に使用できます。30秒以内に処理結果を確認できなければ500を返しますが、受付済み操作を取り消しません。

未対応の起動条件、非空macro、未知パラメーターは400で拒否します。エラー本文はerrorSource/message/messageID/application/extensions形式です。確認済みの入力違反ではKNAK3201/3202/3203/3204/3206/3207/3209-Eを返し、それ以外はJobRunner独自メッセージIDを返します。確定登録の未来世代数指定は後述します。対応する操作・参照は互換表に列挙します。完全なエラーID互換、実JP1比較は未対応です。計画登録はユーザー指定により対象外です。

## 履歴・ログ・操作要求の保持期限

3種類の保持日数は個別指定で、未指定は新たな期限処理を行いません。稼働系が起動後と約1分ごとに処理します。既に開始した履歴退避は、設定を解除しても起動後に再開します。

ルートと全子世代・全試行の結果が確定し、ルート終了時刻からHistoryArchiveAfterDaysを経過した場合、不変Blobへ原JSON・ETagを保存してからTable詳細を削除します。Archivingは処理途中、Archivedは完了です。途中停止は保存済みETagで再開し、変更された行を削除しません。ルートIDと退避先はTableへ残し、世代の再生成を防止します。退避後の詳細はルートの `/archive` から取得してください。通常の `/attempts` には削除済み詳細は含まれません。

Unknown、実行中、保留中、未開始の子がある履歴は退避しません。Failedネットでも未開始後続があれば保持します。手動解決した世代についても元の試行がUnknownなら履歴とそのログを自動削除しません。旧データで終了時刻がない場合も保持します。

LogRetentionDaysは既知の終了試行だけに適用し、履歴退避とは独立しています。期限削除したログは一覧から消えます。履歴Blobはログ本文を含まず、ログ削除後に復元する機能はありません。

完了・拒否した操作はOperationArchiveAfterDays後に本文をBlobへ退避します。同じIDを再受理しないようハッシュ・結果をTableへ残し、照会APIは元の操作を復元して返します。履歴Blob、操作Blob、重複防止用の墓標に自動削除期限は設けていません。

## 確定登録の未来世代数

未来世代数は「まだ開始していないルート世代を何件確保するか」です。1〜99で指定し、開始した分を次のTickで補充します。履歴保存世代数ではありません。例えば3なら、初期に3世代を生成し、最初の1世代が開始したら4世代目を追加します。保留世代も未開始枠に含め、取消・復旧時スキップ・過去に開始した世代は枠から外します。

自動登録には `JobRunner__ConfirmedFutureGenerations=3` を設定します。未指定なら従来の `ConfirmedHorizonDays=32` で動作します。個別の独自API要求例:

```json
{"id":"fixed-three","kind":"Register","target":"/業務/日次/daily","registrationKind":"Confirmed","futureGenerations":3}
```

自動登録と個別API登録は別登録です。同じネットへの追加登録で予定を重複させたくない場合は、既存の自動登録をUnregisterしてから追加してください。

1〜99以外は拒否します。Start/Endとの併用では指定期間内だけを補充します。初回の予定は受付時刻以降から選びます。生成済み予定や一時変更は保持し、同じ採用領域の固定定義で未来分を補充します。探索範囲は最大100年で、予定が不足した場合は存在する分だけ生成し、翌業務日以降に再確認します。元の入力Blob変更は新しい採用領域への切り替えとなり、既存登録への定義変更反映ではありません。予定枠の自動補充は登録解除で停止します。

JP1形式では次のPOSTを提供します。

`/ajs/api/v1/objects/definitions/{encodedUnit}/actions/registerFixedExec/invoke`

```json
{"parameters":{"manager":"localhost","serviceName":"default","futureGeneration":3,"passedDaemonStarts":"NEXTTIME","holding":true}}
```

成功は200・空本文です。再送には既存と同じX-JobRunner-Request-Idを使えます。holdingは初回に生成する最初の世代だけに適用し、以後は定義の保留設定に従います。期間・日時指定、期間と件数の併用、NEXTTIME/IMMEDIATEにも対応します。単独日時指定ではexecIDを返します。非空macroは未対応です。

## ネット単位の一時変更・停止・再実行

独自 `/api/jobrunner/operations` に、対象ネットの世代IDをtargetとして送信します。

| kind | 動作 |
| --- | --- |
| Move | dueまたはRelativeMinutesの一方を指定。既定では対象と未開始の全子孫を同じ時間差で移動。OriginalDueは維持 |
| RunNow | 対象のDueを処理時刻に合わせ、未開始子孫も同じ時間差で移動。保留は解除しない |
| Restore | 対象と全子孫を一時変更前の日時・取消前状態へ復元。未開始の関連ADD世代を取り消す |
| Cancel | 未開始・保留中の対象一式をCancelledにする |
| Stop | 実行中のネット内のプロセスツリーへ停止要求。未開始子孫はCancelled、実行中ネットはInterrupted。試行結果を推測しない |
| Rerun | `rerunMethod: "TOP"` で、対象ネットと全子孫を先頭から実行。過去試行を保持 |

Move/RunNow/Restore/Cancelは、子孫もすべて未開始・保留中の場合だけ受理します。Restoreは子孫に個別設定した日時変更も解除します。強制終了後のUnknownは試行ごとに業務結果を確認してResolveし、その後に新しい操作IDでTOPを要求してください。Stopの操作完了は停止要求・後続抑止の保存完了であり、外部業務の取消完了を保証しません。

100行を超える変更は不変Blobの変更計画とPending操作参照を先に保存し、99行ずつリーダー・Epoch・ETagを確認して適用します。途中停止後は未適用行を再開し、操作完了まで新たなジョブ開始へ進みません。別内容への変更を検出した場合は自動上書きせず停止します。変更計画Blobには自動削除期限を設けていません。

## Linux上でPCジョブを実行する

`ty=pj` はLinuxでも実行できます。JobRunner独自の拡張として、`te` は `/bin/sh -c`、`sc` はローカル実行ファイル、`prm` はその引数として扱います。作業ディレクトリ、環境変数、終了コード判定、ログ保存、停止は既存のローカル実行器と共通です。

```text
unit=pc;{ty=pj;te="printf 'hello from Linux'";}
```

Windows専用のコマンド構文・パス・exe/batをLinux用に変換するものではありません。定義中のコマンドは実行ホストで動作するものを指定してください。Windows上のpjは従来どおりで、Windows実機検証は未実施です。

## 補充定義と一定間隔の登録

生成済み世代を変更せず補充用定義だけ更新するには、定義をimportしてから次の操作を送る。`Target`は登録ID、期待版は登録のDefinitionHash（旧形式では採用版）。競合時は新しい要求IDと確認済みの期待版でやり直す。

```json
{"Id":"change-definition-1","Kind":"UpdateRegistrationDefinition","Target":"登録ID","ExpectedDefinitionHash":"更新前の版","DefinitionHash":"import済みの版"}
```

一定間隔は完了からの待ち時間ではなく、固定された予定時刻からの繰り返し。次の例はUTCの00:00から30分ごと、03:00未満の6回を登録する。停止中の予定は既定では実行しない。終了日時を含まない。

```json
{"Id":"interval-1","Kind":"Register","Target":"/group/net","FixedDue":"2026-11-10T00:00:00Z","IntervalMinutes":30,"IntervalUntil":"2026-11-10T03:00:00Z","PassedDaemonStarts":"NEXTTIME"}
```

`IMMEDIATE`を指定すると未開始の過去予定も実行対象になる。ただしUnknownになった試行を再実行する指定ではない。業務更新の有無を確認し、従来どおり明示的に解決する。

### 保存形式の更新と切り戻し

現行版は稼働系取得時に採用情報のFormatVersionを1/2/3/4から5へ更新する。採用ID、既存世代IDと入力ハッシュは維持する。不変の旧activation.json、activation-v2.json、activation-v3.json、activation-v4.jsonは上書きせず、activation-v5.jsonを保存する。形式5を理解できない旧実行器は再選出時の検査で停止する。

導入時は旧バージョンのAPIと実行器をすべて停止してから新版を起動する。旧APIは新しい要求フィールドを理解できないため、バージョン混在のローリング更新は行わない。旧バイナリへ戻す場合はバイナリだけを戻さず、停止した状態で導入前のTable/管理Blob一式を復元し、導入後の業務実行結果を照合する。形式番号だけを書き戻して新形式の登録を旧実行器に読ませてはいけない。


## 予定変更と変数判定の追加契約

- 独自操作APIのMoveに`RelativeMinutes`（Dueと排他）、`PushedAhead`（ADD/MOVE）、`ChangeLower`（SHIFT/NOTSHIFT）を追加しました。既定MOVE/SHIFTは維持します。JP1 HTTPの既定はADD/NOTSHIFTです。
- UnregisterRootの`DateType`と`Start`/`End`で期間解除します。この操作に限りEndは包含です。RegisterのEndは従来通り排他的です。
- Restoreは一時変更前の時刻と取消状態を復元します。ルートのADDによる追加世代が開始している場合は、部分適用せず拒否します。ネスト単体の復元ではルートの追加世代を取り消しません。
- 予定操作は不変の変更計画とTableの条件付き更新で再開します。参照APIがJOBRUNNER_MUTATION_IN_PROGRESSを返した場合は再取得してください。
- 専用の変数出力ファイルは試行終了後に回収・削除します。値は試行履歴に保持されます。再実行は新しい出力を使い、AJSVARやコマンド内マクロ置換は行いません。
- APIのエラー本文のmessageIDがJOBRUNNERで始まる場合は独自契約です。HTTPタイムアウトは処理取消を意味しません。同じX-JobRunner-Request-Idで結果を再確認してください。

## Web API一覧から追加した運用

[Web API照合表](jp1-webapi-matrix.md)に全19 APIの対応範囲を記載しています。Interruptは稼働中のプロセスを停止せず後続を抑止します。ChangeStatusは元試行を保持した手動結果であり、Unknownには従来のResolveを使います。ChangeDelayMonitorは監視状態の評価であり、タイムアウトや外部通知ではありません。保存形式5への更新は全API・実行器を停止して実施してください。


### 定義監視と参照の変更点

ネットの`sy=[N,]hh:mm|M/U/C分数`、`ey`と`fd=分数`を読み込み、採用ルールの監視設定を世代へ固定する。M=ROOT、U=TOP、C=OWN。M/Uのルート指定と一般ジョブのfdは未対応。補充用定義更新は既存世代やAPIで変更した設定に遡及しない。再実行・MOVE/ADD・復元では世代の設定を保持する。外部通知やプロセス強制終了を行う機能ではない。

statuses一覧は1000件超でもHTTP 200で返り、`all=false`なら検索条件を絞って再取得する。GETの相対監視時間・所要時間は分数文字列へ変更するため、従来hh:mmを期待していた呼出元は更新する。POSTの監視変更は従来のhh:mm入力を維持する。REはJP1のワイルドカードであり、正規表現のグループや文字クラスは扱わない。
