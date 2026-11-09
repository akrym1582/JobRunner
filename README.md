# JobRunner

.NET 10 / ASP.NET Coreで動く、JP1/AJS3 V12定義を入力とする非公式のジョブスケジューラーです。Web APIと運用・定義編集用Web UIを提供し、同じマシン上のプロセスを実行します。状態はAzure Blob/Table Storageへ保存します。

## できること

- **運用監視**。ジョブネットの依存関係・状態、生成済みの月間予定と実績、試行ごとの標準出力・標準エラーを確認できます。開始前のルート実行回は日時変更・取消・変更解除ができます。
- **定義編集**。グループ・ネット・ジョブの作成や編集、移動・複製、スケジュールとカレンダーの設定、ルートと子ネットの連動試算ができます。Unit原文と文字コードを保持し、配置情報付きZIPで入出力できます。グループの取り込みにはJP1の定義テキスト（.txt・.ajsなど）も使用できます。
- **定義の反映と実行制御**。ブラウザーの自動保存とStorage共有下書きを使い分け、差分を確認して運用へ反映します。全体またはグループ単位で予定実行を停止・再開できます。

![定義編集画面](docs/spec/scheduler/web-ui/images/definition-editor.png)

画面操作・画像は [Web UI仕様](docs/spec/scheduler/web-ui/spec.md)、導入・障害復旧は [運用手順](docs/operations.md) を参照してください。JP1互換の範囲と検証状況は [V12対応表](docs/compatibility-v12.md) と [検証結果](docs/verification-results.md) にまとめています。

## バックエンドのビルドとテスト

.NET 10 SDK、Node.js 22、npm、Python 3、curlがあるLinux環境で実行します。Node.jsはUIの開発・ビルドとAzuriteのテストに使用します。本番アプリの実行依存ではありません。

```bash
./scripts/verify.sh
```

Azurite 3.35.0を一時領域へ導入し、Blob/Tableの起動確認、Releaseビルド、StyleCop解析、xUnit、テスト検出数の確認まで実行します。10000〜10002番ポートは空けてください。実Azureの契約・資格情報は不要です。統合テストはアプリを実OSプロセスとして2つ起動し、リース失効を待つため約1分以上かかります。Azurite未起動時のテストスキップはありません。

Web UIの型検査・単体試験・Playwrightの手順は[Web UI](#web-ui)を参照してください。CIでは両方を実行します。

既存Azuriteへ接続してテストする場合:

```bash
dotnet restore JobRunner.slnx
dotnet build JobRunner.slnx -c Release --no-restore
dotnet test JobRunner.slnx -c Release --no-build
```

## 起動

AzuriteまたはAzure Storageの接続先を設定して起動します。入力Blobの設定・配置は通常起動には不要です。

```bash
export JobRunner__ConnectionString='UseDevelopmentStorage=true'
export JobRunner__ScheduleTimeMode=48
export ASPNETCORE_URLS=http://127.0.0.1:5080
dotnet run --project src/JobRunner --no-launch-profile
```

初回は「定義未登録・停止中」です。画面の「定義編集」で新規作成するか、`examples/daily.txt`（配置先`/業務/日次`）と`examples/calendar.txt`（配置先`/`）を一式で読み込み、差分を確認して運用へ反映してください。その後「全体を再開」し、対象のグループも再開してください。新規グループは反映直後は停止状態です。通常の再起動はStorageの定義と状態を復元し、入力Blobは読みません。欠損・破損・実行設定の不一致はFaultedとして停止します。

「全体を一時停止」は同じSchedulerIdの全ジョブの新規開始を止めます。起動済みプロセスは終了まで動作し、結果を回収します。再開すると、再開時刻より前の未開始ルート（保留も含む）は見送り、開始済みネットの後続を続行します。定義反映や再起動では停止を解除しません。

編集画面は複数ジョブ・子孫ネットのコピペ、ファイルをまたぐ複製、月表示カレンダーと継承元の確認に対応します。内部接続だけを複製し、除外接続・継承先を確認して貼り付けます。複製ルートは予定なし・保留ありです。[定義運用の仕様](docs/spec/scheduler/definition-lifecycle/spec.md)を参照してください。

現行の保存形式は8です。形式1〜7の運用データは自動移行されないため、新規Storage領域に導入してください。詳細は[保存形式の更新と切り戻し](docs/operations.md#保存形式の更新と切り戻し)を参照してください。

サンプルはLinux用です。PCジョブ（`ty=pj`）もLinux上のコマンドとして実行できます。Windowsの実機検証は未実施です。

計画実行登録はユーザー指定により対象外です。確定登録に未来世代数（1〜99）の指定と自動補充、ネット一式の日時変更・即時化・取消・強制終了・TOP再実行を追加しました。

実装済みの範囲は、履歴・ログ・操作要求の保持期限処理、失敗ジョブ再実行時の祖先ネット復帰、所有者付き定義ヘッダー、JP1形式の即時登録・保留変更APIです。1,000実ジョブの集中負荷も全テストに含めています。

## 構成と運用

- [現行仕様と受入条件](docs/spec/scheduler/spec.md)
- [設定・API・障害復旧](docs/operations.md)
- [JP1 V12対応表](docs/compatibility-v12.md)
- [検証結果・残る確認](docs/verification-results.md)

アプリ本体とxUnitテストはそれぞれ1つのcsprojです。API認証、リモート実行、SQLite、Quartz、Hangfireは導入していません。

## Web UI

`src/JobRunner/clientapp` に監視・定義編集画面を同梱しています。JP1定義のel座標を使ってReactのSVGでジョブネットを表示し、状態取得にaspidaの生成クライアントと`@aspida/swr`を使用します。認証はありません。実行ログの参照・状態絞り込み、ルート実行回の日時変更・予定取消・変更解除と、前述の定義の下書き編集を提供します。

.NET 10 SDKとNode.js 22.12以降を用意し、初回は依存関係をインストールします。Storageの設定は上記の起動手順と共通です。初回定義は画面から反映します。

```bash
cd src/JobRunner/clientapp
npm ci
cd ..
dotnet watch run
```

Viteは自動起動します。`http://localhost:5173`を開くと、`/api`を.NETの5080番へプロキシします。APIポートを変更する場合は`JOBRUNNER_API_URL`も合わせてください。Swagger UIは開発時の`http://localhost:5080/swagger`です。

APIを変更した場合は、次の順序でスキーマとクライアントを更新します。スキーマ出力ではSchedulerWorkerを起動せず、Storageには接続しません。

```bash
cd src/JobRunner/clientapp
npm run export-api
npm run generate-api
npm run lint
npm run format:check
npm test
npm run build
```

実APIと専用Azuriteを使用するブラウザ試験:

```bash
dotnet build JobRunner.slnx -c Release
cd src/JobRunner/clientapp
npx playwright install chromium
npm run test:e2e
npm run screenshots
```

試験は11300〜11302・5181・5174番ポートと一時ディレクトリを使用します。実Azureは使用しません。`PLAYWRIGHT_CHROMIUM_EXECUTABLE`で既存Chromiumを指定できます。画像は[Web UI仕様](docs/spec/scheduler/web-ui/spec.md)から参照できます。通常試験では文書画像を上書きせず、`screenshots`で明示更新します。

```bash
dotnet publish src/JobRunner -c Release -o artifacts/publish
cd artifacts/publish
dotnet JobRunner.dll
```

publish時にフロントエンドをビルドし、`wwwroot`へ同梱します。配信サーバーにNode.jsは不要です。APIと画面は同じ.NETホストから配信されます。

図の表示には親ネットの `el=ユニット名,種別,+H +V;` が必須です。座標がない・不正な場合は図にエラーを表示します（ジョブ実行には影響しません）。ELKによる自動配置は行いません。マンスリー画面では、サービスのタイムゾーンで日付ごとに生成済みの確定予定・実績を表示します。セルを選択して一覧を確認し、実行回の図へ移動できます。未生成の将来予定の予測は行いません。
