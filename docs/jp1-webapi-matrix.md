# JP1 Web API一覧との照合

2026-11-06。[ユーザー指定の一覧](https://itpfdoc.hitachi.co.jp/manuals/3021/30213b1920/AJSO0266.HTM)は **Version 11 / 3021-3-B19-20 / 6.6.1** の19 APIです。本文とリンク先を取得して照合しました。JobRunnerのV12向け仕様をV11へ置換せず、共通のAPI契約を補完しています。V12には追加APIがあり、この19行はV12全APIの一覧ではありません。

以下のパスは `/ajs/api/v1` からの相対パスです。`{unit:execID}` はユニット完全名と実行IDをURI符号化した値です。「一部対応」は列挙した限定契約を指し、全引数・全状態・エラーID・認証の完全互換を意味しません。**実JP1比較は全項目未実施です。**

| 項番 / API種別 | API | メソッド・パス | 判定と境界 |
| --- | --- | --- | --- |
| 1 / SC-004 | ユニット一覧 | GET `objects/statuses` | 一部対応。名前・階層・種別・世代・状態・遅延で検索。最大1000件・超過は200/all=false。RE（ワイルドカード）とGRP_*状態集合を追加。Agent、リリース、接続情報等は未対応 |
| 2 / SC-007 | ユニット情報 | GET `objects/statuses/{unit:execID}` | 一部対応。現在の採用領域とその退避履歴。遅延設定・有無も返す。登録日時・ジョブ番号・待ち合わせ・接続情報の未取得値も返す。実機比較は未実施 |
| 3 / SC-010 | 実行結果詳細 | GET `objects/statuses/{unit:execID}/actions/execResultDetails/invoke` | 今回追加。最新試行のstdout/stderrを区別して返す。5MiBで切り捨て、all=false。実行中・Unknownは409。JP1実機の詳細本文とは完全一致しない |
| 4 / SC-017 | 計画実行登録 | POST `objects/definitions/{unit}/actions/registerPlannedExec/invoke` | 対象外。ユーザーが確定登録のみ必要と明示 |
| 5 / SC-018 | 確定実行登録 | POST `objects/definitions/{unit}/actions/registerFixedExec/invoke` | 一部対応。期間、未来世代数、日時、NEXTTIME/IMMEDIATE、初回holding。非空macro・起動条件は未対応 |
| 6 / SC-019 | 即時実行登録 | POST `objects/definitions/{unit}/actions/registerImmediateExec/invoke` | 一部対応。holdingとexecID応答。非空macro・起動条件は未対応 |
| 7 / SC-020 | 登録解除 | POST `objects/definitions/{unit}/actions/cancelRegistration/invoke` | 全体または暦日/実行日期間。除外を保存して補充・復旧で復活させない |
| 8 / SC-012 | 保留属性変更 | POST `objects/statuses/{unit:execID}/actions/changeHold/invoke` | 一部対応。未開始世代のSET/RELEASE |
| 9 / SC-024 | 遅延監視変更 | POST `objects/statuses/{unit:execID}/actions/changeDelayMonitor/invoke` | 今回追加。ネットのNOT/ABSOLUTE/ROOT/TOP/OWNと所要時間。省略は維持、NOTでは時刻無視。定義sy/ey/fdから世代生成時に設定。外部通知・一般ジョブのfdは対象外 |
| 10 / SC-025 | ジョブ状態変更 | POST `objects/statuses/{unit:execID}/actions/changeStatus/invoke` | 今回追加。非実行中・結果既知のローカルジョブ。元試行を保持して手動コードを別保存。動作中・Unknown・退避済み・ネットは拒否。判定ジョブはコードのみ |
| 11 / SC-026 | 日時変更 | POST `objects/statuses/{unit:execID}/actions/changeStartTime/invoke` | 一部対応。絶対/相対、ADD/MOVE、SHIFT/NOTSHIFT、未開始世代 |
| 12 / SC-027 | 即時実行への変更 | POST `objects/statuses/{unit:execID}/actions/execImmediate/invoke` | 一部対応。未開始の世代一式、ADD/MOVE、SHIFT/NOTSHIFT |
| 13 / SC-028 | 実行中止 | POST `objects/statuses/{unit:execID}/actions/cancelExecSchedule/invoke` | 一部対応。未開始の世代一式を取消 |
| 14 / SC-029 | 変更解除 | POST `objects/statuses/{unit:execID}/actions/restoreChangedSchedule/invoke` | 一部対応。変更前状態・時刻を復元し、未開始の関連ADD世代を取消。開始済みを含めば全体拒否 |
| 15 / SC-030 | 中断 | POST `objects/statuses/{unit:execID}/actions/interrupt/invoke` | 今回追加。実行中ルートの後続を抑止し、稼働中ジョブは完了させる。結果回収後にINTERRUPT。Unknownが残れば確定しない |
| 16 / SC-031 | 強制終了 | POST `objects/statuses/{unit:execID}/actions/kill/invoke` | 一部対応。ローカルプロセスツリーへ停止要求。手動停止で結果が未確定ならUNKNOWNを維持し、JP1のKILLを無条件に返さない |
| 17 / SC-032 | 再実行 | POST `objects/statuses/{unit:execID}/actions/rerun/invoke` | 一部対応。対応するルート/子の再実行方式とHOLD/WARNING。実行中・Unknownは拒否 |
| 18 / OT-003 | バージョン | GET `version?manager=...` | 今回追加。公式の4メンバーでJobRunner自身の製品名・アセンブリ版を返す。JP1製品番号としての機能判定には使えない |
| 19 / OT-005 | Managerプロトコル版 | GET `protocolVersion?manager=...` | JP1 Managerへ接続しないため501 / JOBRUNNER_NO_JP1_PROTOCOL。架空のJP1プロトコル番号は返さない |

## 追加APIの根拠と差異

- 実行結果詳細: [V11 7.1.3](https://itpfdoc.hitachi.co.jp/manuals/3021/30213b1920/AJSO0281.HTM)。ログ削除後や出力なしの場合は空文字。保存が不完全な試行ではall=false。試行をまたいで混ぜず、退避した試行も同じログを参照する。
- 遅延監視: [V11 7.1.9](https://itpfdoc.hitachi.co.jp/manuals/3021/30213b1920/AJSO0287.HTM)、[定数](https://itpfdoc.hitachi.co.jp/manuals/3021/30213b1920/AJSO0317.HTM)。ABSOLUTEは世代の業務日のサービス時刻00:00からの時刻、相対は対象/上位/ルートの現在の開始予定からの差。開始済みの開始監視変更、ルートのROOT/TOP、曖昧・欠落するサービス時刻を拒否する。非0基準時刻など全カレンダー境界での実機一致は未検証。監視はタイムアウトによる強制終了とは別で、プロセスを停止しない。
- 状態変更: [V11 7.1.10](https://itpfdoc.hitachi.co.jp/manuals/3021/30213b1920/AJSO0288.HTM)、[定数](https://itpfdoc.hitachi.co.jp/manuals/3021/30213b1920/AJSO0318.HTM)。NORMAL/FAIL/WARNING/ABNORMAL/BYPASS/RETURNCODEを扱う。newReturnCodeは符号付き32ビット整数の文字列。Unknownは証拠を伴う独自Resolveでのみ解決し、稼働中プロセスと収集結果の競合を回避する。終了済みネットを自動再開する操作ではない。
- 中断: [V11 7.1.15](https://itpfdoc.hitachi.co.jp/manuals/3021/30213b1920/AJSO0293.HTM)およびV12コマンドリファレンスのajsintrpt。APIの200は中断要求・後続抑止の永続化完了。実プロセス終了は状態参照で確認する。
- 製品情報: [V11 7.1.18](https://itpfdoc.hitachi.co.jp/manuals/3021/30213b1920/AJSO0296.HTM)、[7.1.19](https://itpfdoc.hitachi.co.jp/manuals/3021/30213b1920/AJSO0297.HTM)。対応能力は `/api/jobrunner/compatibility` と本表で判断する。

主な回帰試験: `AdditionalHttpTests`、`AdditionalOperationTests`、`DefinitionVersionTests`。既存のJP1 HTTP、予定・復旧・再実行試験も継続する。HTTP同期200、同一ID再送、エラーでの非変更と永続結果を確認し、単にルートが存在するだけでは対応済みと判定しない。

## 参照・定義監視の追加（保存形式5）

V12 7.4.2表7-57のREは`*`（任意列）、`?`（1文字）、`\`（次の記号を文字として扱う）で全体一致する。一般的な正規表現ではない。末尾だけのエスケープは形式エラー。GRP_*は表7-60の集合と実装済み状態の交差を検索し、応答の状態を集約値へ変えない。1000件の上限はJobRunnerの運用上限であり、検索適用後に完全名・予定日時・世代ID順で返す。

V12 7.3.2に合わせ、GETのdelayedStartTime/delayedEndTimeはABSOLUTEの場合hh:mm、ROOT/TOP/OWNは分数文字列。timeRequiredForExecutionは監視有効時だけ分数文字列。POSTの時刻入力とは形式が異なる。registerTimeは保存済み登録CreatedAtのみを使い、旧データで不明なら空文字。jobNumberは-1（OSのPIDで代用しない）、waitingはNONE、未対応の接続情報は空文字。

V12 5.2.4のsy/ey/fdをローカルネットで受理する。sy/eyは採用ルール別、fdはネット単位。保存済み世代へのAPI上書きは定義更新や復旧で消さず、新しい補充世代のみ更新後の定義を使う。M/Uをルートへ指定することは未対応として拒否する。一般ジョブのfdと外部通知は対象外。
