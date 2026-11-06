# JP1 V12 仕様対応表

一次資料: [JP1/Automatic Job Management System 3 コマンドリファレンス 3021-3-D28-20](https://itpfdoc.hitachi.co.jp/manuals/3021/30213D2820/H03D2820.PDF)。2026-11-05にPDF本文を取得して確認しました。**実JP1との比較は全項目未実施**です。確定登録の補充規則は[導入ガイド3021-3-D21-20](https://itpfdoc.hitachi.co.jp/manuals/3021/30213D2120/H03D2120.PDF)4.1.1(3)も参照しています。

この表の「実装済み・仕様テスト済み」は記載した限定ケースを指します。同じカテゴリの全指定や実機互換を意味しません。

| 対象 | 公式根拠・入力 | 評価規則と制約 | 状態 | テストID |
| --- | --- | --- | --- | --- |
| 階層・引用文字列 | 5.1.1、5.2.6 `unit`、`#"`、`##` | 単純名、階層、行番号、繰り返し属性、文字列内区切り・空白を保持 | 実装済み・仕様テスト済み | DefinitionTests.PreservesQuotedCharactersAndHierarchy、KeepsWhitespaceInsideQuotedValue |
| コメント | 5.1.1 `/* */` | 入れ子、未閉鎖、属性と同じ行を拒否 | 実装済み・仕様テスト済み | DefinitionTests.RejectsMalformedAndCyclicDefinitions |
| 所有者・許可ヘッダー | 5.1.1 | 4桁8進モード、所有者、資源グループを保持。所有者実行はLocalUsersで現OSユーザーへ明示対応。OSユーザー切り替えなし | 一部実装・仕様テスト済み | DefinitionTests |
| 入力絶対名 | 5.1.1 | 配置基点の曖昧な変換をしない | 未実装・拒否 | パーサーで位置付きエラー |
| 種別と通常接続 | 5.2.1、5.2.4 `g/n/j/pj`、`ar=(f=...,t=...,seq)` | 同一ネット直下の接続を解決。重複、欠落、循環を拒否 | 実装済み・仕様テスト済み | DefinitionTests、IntegrationTests |
| 稼働日と休業日 | 5.2.3 `op/cl` | 日付例外、曜日、上位探索。日付例外が曜日より優先するケースを検証 | 実装済み・仕様テスト済み（限定ケース） | ScheduleTests.ExplicitOpenDateOverridesWeekend |
| カレンダー参照と基準時刻 | 5.2.3/5.2.4 `jc/stt` | グループ参照と基準時刻継承、基準06:00の前後・相対時刻を検証 | 実装済み・限定ケース検証 | CalendarBoundaryTests |
| 開始日・処理サイクル | 5.2.4 pp.1053–1055 `sd/cy` | 暦日、登録日、日/週/月/年サイクル。月末とうるう年を検証 | 実装済み・仕様テスト済み（限定ケース） | ScheduleTests.MonthlyLastDayIncludesLeapDay、CalculatesOneThousandDailyJobsForOneYear |
| 曜日、運用日番号、休業日番号 | 5.2.4 `sd` | 指定の解析と計算あり。登録日を跨ぐ組合せの期待値検証が不足 | 実装あり・追加検証未達 | 追加の公式期待値が必要 |
| 振り替え | 5.2.4 pp.1055–1056 `sh/shd` | 前後・中止・休業日実行、猶予日数を実装。年跨ぎ・起算順序と重複排除を検証 | 実装済み・限定ケース検証 | CalendarBoundaryTests、ExtendedScheduleTests、ConfirmedGenerationTests |
| 時刻48時間制 | 5.2.4 `st`、計画16.6 | 23:59/24:00/25:00/47:59をUTCへ変換。業務日を別保存 | 実装済み・仕様テスト済み（基準0:00/06:00） | ScheduleTests.PreservesBusinessDateBeyondMidnight、PreviousBusinessDayCanStillBeInFuture |
| 時刻24時間制の24時以降 | 詳細仕様の追加確認が必要 | JP1で不正とは断定せず、未対応として拒否 | 調査中・未実装 | 実装後に境界テストが必要 |
| 相対日、月区分、基準日、起算、時間間隔 | `+sd/sdd/md/cftd`、独自間隔登録 | 相対業務月、起算、固定開始からの一定間隔を実装。実機比較未実施 | 一部実装・限定ケース検証 | ExtendedScheduleTests、BranchAndIntervalTests |
| ネスト | 5.2.4、設計ガイド3.5.3 | 上位ルール・同一業務日から最早候補を一つ採用 | 一部実装・限定ケース検証 | NestedScheduleTests |
| Linux上のPCジョブ | ユーザー指定の独自拡張 | ty=pjをLinuxでも受理。teは/bin/sh、sc/prmはローカル実行ファイルと引数。Windowsコマンドの変換はしない | 実装済み・Linux統合テスト済み | PcJobTests |
| ローカル実行・終了判定 | 5.2.6 pp.1064–1067 `te/sc/prm/wkp/env/etm/jd/wth/tho` | シェル/実行ファイル分離、両出力収集、コード分類 | 実装済み・仕様テスト済み（Linux teとコード判定） | ExecutionTests.ClassifiesActualExitCodes、LogStorageFailureStopsProducer |
| 自動確定登録・補充・解除維持 | 計画16章（JobRunner独自運用） | 採用IDと基準日固定、決定的な世代キー、カーソル補充、解除行保持 | 実装済み・障害テスト済み | RecoveryTests、ExecutionControlTests、ConfirmedGenerationTests、ReviewRegressionTests。DefinitionMonitorTestsで監視設定の部分生成・長期停止復旧も確認 |
| 即時登録・保留解除 | 独自管理API。JP1互換DTOではない | 操作IDで重複防止、親・先行成功後に開始 | 実装済み・仕様テスト済み | IntegrationTests.RunsRealProcessesThroughStandbyApiAndRecovers |
| 計画登録 | ユーザー指定による範囲変更 | 要求はRejected | 対象外 | 確定登録と既存の即時登録を提供 |
| 一時変更・再実行・停止 | 独自管理API、TOP方式は7.4.10参照 | Move/RunNow/Restore/Cancelを再開可能な変更計画で子孫へ適用。ネットStopはInterruptedとUnknownで結果確認を要求。TOP再実行・末端異常再実行に対応。選択再実行方式とHOLD/WARNINGも追加 | 一部実装・仕様テスト済み | ExecutionControlTests、NetworkOperationTests、OperationMutationTests |
| 確定登録の未来世代数 | 導入ガイド4.1.1(3)、コマンド-g | 1〜99の未開始枠を維持。生成済み予定と履歴を保持。期間併用と補充用定義更新を追加 | 一部実装・仕様テスト済み | ConfirmedGenerationTests |
| JP1確定登録HTTP | 7.1.6 | 期間・件数・日時、NEXTTIME/IMMEDIATE。日時はexecID、その他は空本文 | 一部実装・契約テスト済み | Jp1ContractTests.FixedRegistrationMaintainsRequestedFutureGenerations |
| 結果不明の解決 | 計画7章（独自運用） | 試行ID・期待状態・理由必須。自動再実行せず後続待機 | 実装済み・仕様テスト済み | RecoveryTests.StartingAttemptBecomesUnknownWithoutRerun、IntegrationTests |
| JP1即時登録・保留変更HTTP | 6.5.2、7.1.7、7.1.9、7.4.8 | URL/parameters/同期200/execID/SET・RELEASEを実装。未対応パラメーター拒否。エラーIDは一部独自、実JP1未比較 | 一部実装・契約テスト済み | Jp1ContractTests |
| その他のJP1 HTTP操作 | 7.1.8、7.1.12〜15、運用操作各節 | ADD/MOVE、SHIFT/NOTSHIFT、絶対/相対日時、取消復元、暦日/実行日期間解除を追加 | 一部実装・契約テスト対象 | Jp1ReadAndScheduleTests、CompatibilityExtensionTests、OperationMutationTests |
| 履歴・ログ・操作保持期限 | 計画9章（独自運用） | 未指定は無効。Blob保存後に詳細削除、墓標維持。Unknown除外、ETag付き再開 | 実装済み・障害テスト済み | RetentionTests |
| 条件分岐・異常時後続 | 5.2.8、5.2.1、設計ガイド2.4.6/7.5 | 終了コード・ファイル・変数判定、rj/rp/rn/rjdj。変数受渡しは独自JSON方式 | 一部実装・限定契約 | CompatibilityExtensionTests、BranchAndIntervalTests |
| 待ち合わせ・イベント | 今回の範囲外 | 種別・属性を拒否 | 対象外 | 今回追加しない |
| 別形式Schedule/Calendar | 実出力例未入手 | 独自JSONをJP1形式として受理しない | 調査中・未実装 | 匿名化したV12出力と形式根拠が必要 |
| 認証・Agent通信 | 計画2章 | 実装しない | 対象外 | プロジェクト依存関係を確認 |

原文は明示文字コードと取得バイト列で保存します。UTF-8、UTF-16のBOM、Shift_JISはテスト用原文で読み書き検証済みです。実データ・実JP1での比較は未検証です。未知属性は保持した後で有効化を拒否します。表示属性`cm/sz/el`は保持しますが実行に使いません。

Web UIの図は`el`の座標と`ar`の接続を使用します。座標の対応範囲は[Web UI仕様](spec/scheduler/web-ui/spec.md)を参照してください。V12 5.2.2の座標範囲を検証しますが、JP1 Viewのグリッド補正・画面再現の互換性は提供しません。座標不足は図の表示エラーとなり、定義の実行には影響しません。

## 今回のHTTP互換境界

互換の対象はV12のローカルユニットと確定登録です。実JP1での比較は未実施で、以下の文書仕様と受入試験を基準とします。

| 領域 | 対応する範囲 | 対象外・差異 |
| --- | --- | --- |
| 参照 | 現在の採用領域のGET statuses一覧、unitName:execIDの単体。階層、定義/状態、名前比較EQ/BW/EW/NE/CO/NC/RE、GRP_*状態集合、種別、NO/STATUS/RESULT/PERIOD/EXECID、個別の対応状態 | 旧採用領域の検索、Agent/リリース検索、searchConnectInfo=YES。最大1000件・超過は200/all=falseの部分返却 |
| 世代選択 | STATUSはVIEWSTATUSRANGE=allの実行中→再実行待ち→次回予定。RESULTは最新終了。PERIODはルートの実行期間との重なり | today設定、夏時間の曖昧・欠落時刻のJP1補正。旧データの欠落した実開始日時は精密な期間互換対象外 |
| 状態 | TIMEWAIT/TERMWAIT/EXECWAIT/HOLDING/RUNNING/WACONT/ABCONT/NORMAL/NORMALFALSE/WARNING/ABNORMAL/FAIL/KILL/INTERRUPT/UNKNOWN/UNEXEC/BYPASS、未登録NOENTRY・未計画NOPLAN | Agentキュー、閉塞、順序不正、イベント/監視・起動条件の状態。履歴退避状態は業務結果へ戻して表示 |
| 入力エラーID | KNAK3201（必須）、3202（バイト長）、3203（数値範囲）、3204（禁止文字）、3206（形式）、3207（比較値不足）、3209（排他）の確認済み条件 | KNAK3208（対象APIの配列上限を未確認）、全JP1エラー、本文の完全一致、複数違反の報告順、付加情報コードの全面互換。対応IDは各検証箇所に限定 |
| その他のエラー | 公式のerrorSource/message/messageID/application/extensions形状。保存障害・状態競合・未対応はJOBRUNNER由来のID | 認証/権限・リモート通信・JP1内部サービス障害の再現 |

`UNKNOWN`の表示は「終了状態不明」です。API定数表の「終了状態不正」という表記とは差がありますが、[状態取得失敗時の説明](https://itpfdoc.hitachi.co.jp/manuals/3021/30213L4700/AJSK0045.HTM)とV12導入ガイドのunknownに対応付けます。確認前に自動再実行・リカバリー起動はしません。強制終了はプロセス終了を確認できたタイムアウトの場合だけKILLとし、手動Stop等で結果が未確定ならUNKNOWNを維持します。

変数受渡しはJP1のAJSVAR互換ではありません。`JOBRUNNER_OUTPUT_FILE`へUTF-8のJSONオブジェクト（例: `{"AJS2RESULT":"7"}`）を出力し、`ejv=?AJS2RESULT?`で参照します。最大32変数、各値255バイト、ファイル128KiB。未定義/空文字は有無判定で値なし、必要な数値・値がない比較は判定エラーです。ファイル判定は実行ホストの絶対パスのみです。

## 公式Web API一覧からの追加対応

V11の19 API一覧との突合結果、追加API、対象外と残る差異は[Web API照合表](jp1-webapi-matrix.md)を参照してください。中断、遅延監視変更、ジョブ状態変更、実行結果詳細、JobRunnerの製品情報を追加しました。遅延監視の設定変更・状態表示・検索は対応し、sy/ey/fdからのネット監視設定取込も対応。Agent/リリース、一般ジョブのfdと外部通知は対象外です。

## カレンダー境界の検証範囲

`CalendarBoundaryTests`で、基準06:00を参照・継承した24/48時間制、基準時刻の直前/一致/直後、相対時刻、年跨ぎの休日振替・猶予と起算、当月/翌月の業務月末を確認する。同時刻ルールの重複排除は監視設定の採用ルールとともに、件数/日数方式・長期停止からの復旧で確認する。29〜31日と第5曜日の基準日、24時間制の24時以降、夏時間の曖昧・欠落時刻は拒否を維持する。実JP1との比較を済ませた意味ではない。

## 原文を保持する定義編集

Web UIの下書き編集・プレビュー・ダウンロードAPIはJobRunner独自仕様です。V12 3021-3-D28-20の本文を2026-11-06に確認し、5.1.1のunit/コメント、5.2.2のel/sz、5.2.4のar、5.2.6のジョブ属性と引用文字列のキャスト（`#"`、`##`）を使用しています。原文の構文位置を使って編集箇所だけ置換し、既存の取り込み検証を再利用します。保存モデルや採用の処理は変更しません。

追加・ドラッグ時はH=80+160x、V=48+96y（100列・100行以内）へ配置し、必要ならszを拡張します。既存の非グリッド座標は変更するまで維持します。既存の`sz`の`x`表記も読み、拡張時は`*`で記述します。名前変更時はar/elも更新します。構文の区切り文字を含む新規名・変更名は初版では拒否します。

DefinitionEditorTestsとDefinitionEditorApiTestsで無変更バイト一致、UTF-8/UTF-16のBOM、CRLF、Shift_JIS、引用符・#・日本語、原文欠落、複数入力、接続制約、運用データ不変を検証します。これはテスト用定義での文書仕様検証であり、実JP1への投入・JP1 Viewでの表示比較は未実施です。

判定ジョブjdj/rjdjの追加・削除・属性・接続編集は、同資料5.2.8（pp.1077–1081）の24判定方式を対象とします。ejf/ejtは引用文字列、ej/ejc/ejl/ejh/ejm/eju/ejv/eji/ejs/ejg/haは非引用で書き出し、空のejtと未編集の表記を保持します。新規はej=gt・ejc=0、接続は明示操作です。ef/nfに切り替えてejcを削除する場合は確認します。実行器の接続制約・数値境界を既存の取り込み検証で確認し、プレビュー時に判定を実行しません。変数受渡しは前述の独自JSON方式です。DefinitionEditorJudgmentTestsとブラウザー試験で検証します。

編集投影のeditorVersion=2で判定属性を扱います。未指定/1は旧契約で判定属性を読み取り専用とし、旧下書きの欠落属性で原文を消しません。ブラウザーは元の定義ハッシュで属性を補完してから編集可能にし、原文を取得できなければ既存の編集を保持して保存を停止します。移行後もIndexedDBの保存世代比較を適用します。

ネット階層編集はeditorVersion=3で、通常ルートnと子ネットn/rnの追加、既存ネットの改名・コメント・子孫削除を扱います。V12 5.2.4のsd=0,ud（p.1053）、ha=y、deの省略時y（pp.1058–1059）を確認し、新規ルートは予定なし・保留あり、子は親依存とします。既存の予定・jc・ヘッダーを保持し、ar/elだけを改名・削除に追従させます。実行モデルが拒否するルートrnは作成しません。既存ファイルへの局所挿入だけを扱い、階層移動・種別変更・ファイル移動・新規ファイル・グループ編集は含めません。別入力のTargetPathがネットまたはその配下を指す場合、関係するネットの改名・削除を拒否します。旧版1/2の要求・下書きは保持して移行します。DefinitionEditorNetworkTestsと実HTTP・ブラウザー試験で検証します。


予定・カレンダーの下書き編集はeditorVersion=4で、sd/st/cy/sh/shd/cftd/ln/ed/jc/deと、既存グループのop/cl/stt/sdd/mdを扱います。根拠はV12 3021-3-D28-20、5.2.3（pp.1049–1050）と5.2.4（pp.1053–1059）です。属性を省略したまま保持し、ルール番号の再採番や関連属性の自動削除をしません。原文のない仮想グループは編集対象に出しません。sy/ey/fd、保留・多重起動など、編集対象外の属性は保持します。旧版1/2/3の要求と下書きを保護します。

下書き試算は現行ScheduleCalculatorによる未登録のルート候補であり、確定登録や実行予定の照会ではありません。登録基準日と排他的終了業務日を明示し、1994〜2036年・最大366日・2000候補の画面用制限を設けます。ネスト単独・起動条件・独自間隔登録・保留状態を含む実行予測はしません。29〜31日/第5曜日の基準日、24時間制の24時以降、夏時間の曖昧・欠落時刻の未対応・拒否を維持します。原文の文字コードと未編集部分、複数ルール・上位参照、旧下書き・競合、再取込、試算範囲と運用データ不変をDefinitionEditorTimingTests、実HTTP、ブラウザーで検証します。実JP1への投入比較は未実施です。
