# JobRunner

.NET 10 / ASP.NET Coreで動く、JP1/AJS3 V12定義を入力とする非公式のジョブスケジューラーです。Web APIのみを提供し、同じマシン上のプロセスを実行します。状態はAzure Blob/Table Storageへ保存します。

**実装計画全体は未完了です。** 現在の実装・テスト済み範囲と未達項目は [検証結果](docs/verification-results.md)、JP1仕様との対応は [互換表](docs/compatibility-v12.md) を参照してください。完全互換や業務処理のexactly-onceは保証しません。

## ビルドと全テスト

.NET 10 SDK、Node.js 22、npm、Python 3、curlがあるLinux環境で実行します。Node.jsはAzuriteのテスト用途だけで、アプリの実行依存ではありません。

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

稼働系だけが入力一式を検証し、ルートネットを自動確定登録します。入力がない・不正・未対応の場合、APIは起動しますがスケジューラーは`Faulted`となり実行しません。サンプルはLinux用です。Windowsの実機検証は未実施です。

計画実行登録はユーザー指定により対象外です。確定登録に未来世代数（1〜99）の指定と自動補充、ネット一式の日時変更・即時化・取消・強制終了・TOP再実行を追加しました。

実装済みの範囲は、履歴・ログ・操作要求の保持期限処理、失敗ジョブ再実行時の祖先ネット復帰、所有者付き定義ヘッダー、JP1形式の即時登録・保留変更APIです。1,000実ジョブの集中負荷も全テストに含めています。

## 構成と運用

- [実装計画（提供原文）](docs/implementation-plan.md)
- [現行仕様と受入条件](docs/spec/scheduler/spec.md)
- [設定・API・障害復旧](docs/operations.md)
- [JP1 V12対応表](docs/compatibility-v12.md)
- [検証結果・未達項目](docs/verification-results.md)

本体は1つのcsproj、xUnitテストは別の1つのcsprojです。UI、API認証、リモート実行、SQLite、Quartz、Hangfireは導入していません。
