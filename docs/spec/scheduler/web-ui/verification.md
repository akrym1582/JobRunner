# Web UI 検証記録

対象: main `69218b2` を取り込んだ上でのWeb UI追加差分。2026-11-06、Linux、.NET SDK 10.0.403、Node.js 24.22.1、Chromium。実Azure・Windows・実JP1との比較は実施していない。

## 自動検証

- `./scripts/verify.sh`: ReleaseビルドとStyleCopが成功し、Azuriteを使った既存試験とUiReadTests・DefinitionLayoutTestsの221件が成功（失敗0・スキップ0）。固定定義版と退避済み結果、Unknown表示、現在定義との分離を含む。
- `npm run lint` / `npm run format:check` / `npm test` / `npm run build`: ESLint、Prettier、Vitest 4件、型検査、Viteビルドが成功。
- `npm run screenshots`: Playwright 6件が成功。実APIと専用Azuriteで図・階層・予定変更・取消・変更解除・再送・開始済み拒否・通信復旧・受付応答喪失を検証。追加の描画試験では200ノードと条件線を使い、キーボード選択と5秒後の配置維持を確認。
- 座標の欠落・重複・範囲・対象/種別不一致は.NETで検証。Playwrightでは座標欠落時の明示エラーと月間画面の閲覧継続を確認。月間の階層開閉、複数回、正常終了実績、未開始取消、月をまたぐ予定移動、業務日と暦日の分離、閏年・年越し、図への移動、通信復旧を検証。退避済み実績と旧版のユニットは実HTTP試験で確認。
- OpenAPIの出力とaspida生成が成功。生成スキーマはUI専用DTOの必須項目とnullableを保持する。取得処理はuseAspidaSWRを使用する。

## ホスト・配信の実動確認

- `dotnet publish` でnpm ci・型検査・Viteビルドとwwwrootへの同梱を確認。
- publish先で.NETを起動し、トップページ、直接URL、JavaScript、health APIをHTTPで確認。未知の `/api`、`/ajs/api`、静的ファイルが404になることを確認。
- `dotnet watch run` を起動し、.NET側への初回アクセス前に5173番でViteが応答すること、ViteからのAPIプロキシ、開発用Swaggerを確認。
- ChromiumでCSSを一時変更し、ページを再読み込みせずHMRで反映されることを確認。変更したCSSは復元済み。
- ホスト終了後にViteのポートが閉じることを確認。パイプ経由のdotnet-watch試験ランチャーは親監視プロセスが終了を待つため、試験用プロセスグループを後片付けした。

## 修正・再検証した問題

- Blob SDKの最新版がAzuriteの対応API版を超えたため、テスト用SDKを12.26.0へ固定した。
- 即時登録のテストデータに必須のDueを追加し、開始済み拒否の試験が成功した。
- SpaProxyだけでは初回アクセス前のVite起動を保証できなかったため、開発プロファイル限定のHostedServiceへ変更。自動起動・HMR・停止を再検証した。
- 受付応答喪失時は確認ダイアログを閉じ、同じ操作IDの照会を継続する。通信エラー後の再照会も含めPlaywrightで確認した。

- 座標欠落ケースで、aspida/fetchのHTTPエラーを正常データとして受け取って画面が空白になる問題を検出した。失敗時のtraceと画像を保持して調査し、`throwHttpErrors: true`に修正。エラー理由表示と月間画面への移動を含む6件を再検証した。

- API再生成の同一性チェックを画像更新のブラウザ試験と重ねた際、生成ファイルの削除・再作成によるViteの再読み込みで選択状態の試験が失敗した。traceとログを保持し、再生成後にブラウザ試験を単独で実行して再検証した。CIは生成とブラウザ試験を順番に実行する。

## 画面確認

[仕様と画面イメージ](spec.md)に6枚を保存した。フォントと定義座標の反映後に撮影し、文字切れ、矢印、階層、選択、予定変更結果を目視確認した。画面サイズは1440×900、ページ全体を保存するため画像の高さは内容によって増える。

CIでも.NET試験、OpenAPI再生成差分、ESLint、Prettier、Vitest、型検査、Playwrightを実行する。ドラフトPRの最新コミットの結果と実行リンクはPRに記録する。
