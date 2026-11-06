# JobRunner

.NET 10 / ASP.NET Coreで動く、JP1/AJS3 V12定義を入力とする非公式のジョブスケジューラーです。Web APIと監視用Web UIを提供し、同じマシン上のプロセスを実行します。状態はAzure Blob/Table Storageへ保存します。

**実装計画全体は未完了です。** 現在の実装・テスト済み範囲と未達項目は [検証結果](docs/verification-results.md)、JP1仕様との対応は [互換表](docs/compatibility-v12.md) を参照してください。完全互換や業務処理のexactly-onceは保証しません。

## ビルドと全テスト

.NET 10 SDK、Node.js 22、npm、Python 3、curlがあるLinux環境で実行します。Node.jsはUIの開発・ビルドとAzuriteのテストに使用します。本番アプリの実行依存ではありません。

```bash
./scripts/verify.sh
```

Azurite 3.35.0を一時領域へ導入し、Blob/Tableの起動確認、Releaseビルド、StyleCop解析、xUnit、テスト検出数の確認まで実行します。10000〜10002番ポートは空けてください。実Azureの契約・資格情報は不要です。統合テストはアプリを実OSプロセスとして2つ起動し、リース失効を待つため約1分以上かかります。Azurite未起動時のテストスキップはありません。

既存Azuriteへ接続してテストする場合:

```bash
dotnet restore JobRunner.slnx
dotnet build JobRunner.slnx -c Release --no-restore
dotnet test JobRunner.slnx -c Release --no-build
```

## 起動

Azuriteを起動し、Azure Storage Explorer等で`jp1-definitions`コンテナーに次の2ファイルを配置します。

| ローカルファイル | Blob名 |
| --- | --- |
| `examples/daily.txt` | `jobs/daily.txt` |
| `examples/calendar.txt` | `calendars/business.txt` |

```bash
export JobRunner__ConnectionString='UseDevelopmentStorage=true'
export JobRunner__DefinitionSources__0__Kind=Unit
export JobRunner__DefinitionSources__0__TargetPath=/業務/日次
export JobRunner__DefinitionSources__0__BlobPath=jp1-definitions/jobs/daily.txt
export JobRunner__DefinitionSources__1__Kind=Unit
export JobRunner__DefinitionSources__1__TargetPath=/
export JobRunner__DefinitionSources__1__BlobPath=jp1-definitions/calendars/business.txt
export JobRunner__ScheduleTimeMode=48
export ASPNETCORE_URLS=http://127.0.0.1:5080
dotnet run --project src/JobRunner --no-launch-profile
```

稼働系だけが入力一式を検証し、ルートネットを自動確定登録します。入力がない・不正・未対応の場合、APIは起動しますがスケジューラーは`Faulted`となり実行しません。サンプルはLinux用です。PCジョブ（`ty=pj`）もLinux上のコマンドとして実行できます。Windowsの実機検証は未実施です。

計画実行登録はユーザー指定により対象外です。確定登録に未来世代数（1〜99）の指定と自動補充、ネット一式の日時変更・即時化・取消・強制終了・TOP再実行を追加しました。

実装済みの範囲は、履歴・ログ・操作要求の保持期限処理、失敗ジョブ再実行時の祖先ネット復帰、所有者付き定義ヘッダー、JP1形式の即時登録・保留変更APIです。1,000実ジョブの集中負荷も全テストに含めています。

## 構成と運用

- [実装計画（提供原文）](docs/implementation-plan.md)
- [現行仕様と受入条件](docs/spec/scheduler/spec.md)
- [設定・API・障害復旧](docs/operations.md)
- [JP1 V12対応表](docs/compatibility-v12.md)
- [検証結果・未達項目](docs/verification-results.md)

本体は1つのcsproj、xUnitテストは別の1つのcsprojです。API認証、リモート実行、SQLite、Quartz、Hangfireは導入していません。

## Web UI

`src/JobRunner/clientapp` に監視画面を同梱しています。JP1定義のel座標を使ってReactのSVGでジョブネットを表示し、状態取得にaspidaの生成クライアントと`@aspida/swr`を使用します。認証はありません。定義編集は行わず、ルート実行回の日時変更・予定取消・変更解除を提供します。

.NET 10 SDKとNode.js 22.12以降を用意し、初回は依存関係をインストールします。Storageと入力定義の設定は上記の起動手順と共通です。

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
