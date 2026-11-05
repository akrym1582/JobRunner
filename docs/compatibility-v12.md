# JP1 V12 仕様対応表

一次資料: [JP1/Automatic Job Management System 3 コマンドリファレンス 3021-3-D28-20](https://itpfdoc.hitachi.co.jp/manuals/3021/30213D2820/H03D2820.PDF)。2026-11-05にPDF本文を取得して確認しました。**実JP1との比較は全項目未実施**です。

この表の「実装済み・仕様テスト済み」は記載した限定ケースを指します。同じカテゴリの全指定や実機互換を意味しません。

| 対象 | 公式根拠・入力 | 評価規則と制約 | 状態 | テストID |
| --- | --- | --- | --- | --- |
| 階層・引用文字列 | 5.1.1、5.2.5 `unit`、`#"`、`##` | 単純名、階層、行番号、繰り返し属性、文字列内区切り・空白を保持 | 実装済み・仕様テスト済み | DefinitionTests.PreservesQuotedCharactersAndHierarchy、KeepsWhitespaceInsideQuotedValue |
| コメント | 5.1.1 `/* */` | 入れ子、未閉鎖、属性と同じ行を拒否 | 実装済み・仕様テスト済み | DefinitionTests.RejectsMalformedAndCyclicDefinitions |
| 所有者・許可ヘッダー | 5.1.1 | 4桁8進モード、所有者、資源グループを保持。所有者実行はLocalUsersで現OSユーザーへ明示対応。OSユーザー切り替えなし | 一部実装・仕様テスト済み | DefinitionTests |
| 入力絶対名 | 5.1.1 | 配置基点の曖昧な変換をしない | 未実装・拒否 | パーサーで位置付きエラー |
| 種別と通常接続 | 5.2.1、5.2.4 `g/n/j/pj`、`ar=(f=...,t=...,seq)` | 同一ネット直下の接続を解決。重複、欠落、循環を拒否 | 実装済み・仕様テスト済み | DefinitionTests、IntegrationTests |
| 稼働日と休業日 | 5.2.3 `op/cl` | 日付例外、曜日、上位探索。日付例外が曜日より優先するケースを検証 | 実装済み・仕様テスト済み（限定ケース） | ScheduleTests.ExplicitOpenDateOverridesWeekend |
| カレンダー参照と基準時刻 | 5.2.3/5.2.4 `jc/stt` | グループ参照の存在確認と基準時刻継承あり。組合せの詳細比較が不足 | 実装あり・追加検証未達 | 境界・継承・参照の組合せを追加する必要あり |
| 開始日・処理サイクル | 5.2.4 pp.1053–1055 `sd/cy` | 暦日、登録日、日/週/月/年サイクル。月末とうるう年を検証 | 実装済み・仕様テスト済み（限定ケース） | ScheduleTests.MonthlyLastDayIncludesLeapDay、CalculatesOneThousandDailyJobsForOneYear |
| 曜日、運用日番号、休業日番号 | 5.2.4 `sd` | 指定の解析と計算あり。登録日を跨ぐ組合せの期待値検証が不足 | 実装あり・追加検証未達 | 追加の公式期待値が必要 |
| 振り替え | 5.2.4 pp.1055–1056 `sh/shd` | 前後・中止・休業日実行、猶予日数を実装。月境界と複数ルール競合は未検証 | 実装あり・追加検証未達 | 追加の仕様テストが必要 |
| 時刻48時間制 | 5.2.4 `st`、計画16.6 | 23:59/24:00/25:00/47:59をUTCへ変換。業務日を別保存 | 実装済み・仕様テスト済み（基準0:00） | ScheduleTests.PreservesBusinessDateBeyondMidnight、PreviousBusinessDayCanStillBeInFuture |
| 時刻24時間制の24時以降 | 詳細仕様の追加確認が必要 | JP1で不正とは断定せず、未対応として拒否 | 調査中・未実装 | 実装後に境界テストが必要 |
| 相対日、月区分、基準日、起算、時間間隔 | `+sd/sdd/md/cftd`ほか | 未対応の実行属性を拒否 | 未実装 | 拒否を継続し、仕様確認後に追加 |
| ネスト | 5.2.4 | 通常依存階層を実装。ネスト独自スケジュールは拒否 | 一部実装・未達 | ネストの予定・上位ルール対応テストが必要 |
| ローカル実行・終了判定 | 5.2.5 pp.1064–1067 `te/sc/prm/wkp/env/etm/jd/wth/tho` | シェル/実行ファイル分離、両出力収集、コード分類 | 実装済み・仕様テスト済み（Linux teとコード判定） | ExecutionTests.ClassifiesActualExitCodes、LogStorageFailureStopsProducer |
| 自動確定登録・補充・解除維持 | 計画16章（JobRunner独自運用） | 採用IDと基準日固定、決定的な世代キー、カーソル補充、解除行保持 | 実装あり・追加検証未達 | RecoveryTestsで起動登録を検証。ExecutionControlTestsで解除・保留・予定変更の再起動復元も検証。補充中断は未検証 |
| 即時登録・保留解除 | 独自管理API。JP1互換DTOではない | 操作IDで重複防止、親・先行成功後に開始 | 実装済み・仕様テスト済み | IntegrationTests.RunsRealProcessesThroughStandbyApiAndRecovers |
| 計画登録 | 7.1.5および導入ガイド | 確定登録で代用しない。要求はRejected | 未実装 | 変更反映と世代再計算を追加する必要あり |
| 一時変更・再実行・停止 | 独自管理API | Move/Restoreの原予定保持、末端再実行でFailed祖先復帰。ネット一括再実行は拒否。Cancel/Stopの全組合せは未検証 | 一部実装・仕様テスト済み | ExecutionControlTests |
| 結果不明の解決 | 計画7章（独自運用） | 試行ID・期待状態・理由必須。自動再実行せず後続待機 | 実装済み・仕様テスト済み | RecoveryTests.StartingAttemptBecomesUnknownWithoutRerun、IntegrationTests |
| JP1即時登録・保留変更HTTP | 6.5.2、7.1.7、7.1.9、7.4.8 | URL/parameters/同期200/execID/SET・RELEASEを実装。未対応パラメーター拒否。エラーIDは一部独自、実JP1未比較 | 一部実装・契約テスト済み | Jp1ContractTests |
| その他のJP1 HTTP操作 | 7.1.5ほか | 未対応の操作を成功扱いしない | 未実装 | 実装・契約テストが必要 |
| 履歴・ログ・操作保持期限 | 計画9章（独自運用） | 未指定は無効。Blob保存後に詳細削除、墓標維持。Unknown除外、ETag付き再開 | 実装済み・障害テスト済み | RetentionTests |
| 条件分岐・異常時後続・待ち合わせ・イベント | 5.2.4ほか | 未対応の属性・種別・接続を拒否 | 未実装 | 仕様調査・実装・契約テストが必要 |
| 別形式Schedule/Calendar | 実出力例未入手 | 独自JSONをJP1形式として受理しない | 調査中・未実装 | 匿名化したV12出力と形式根拠が必要 |
| 認証・Agent通信 | 計画2章 | 実装しない | 対象外 | プロジェクト依存関係を確認 |

原文は明示文字コードと取得バイト列で保存します。UTF-8はテスト済み、Shift_JIS等は復号機能のみで実データ比較は未検証です。未知属性は保持した後で有効化を拒否します。表示属性`cm/sz/el`は保持しますが実行に使いません。
