# 実装・検証計画
1. UI向け読取APIを追加し、既存の固定定義・状態変換・操作キューを再利用する。保存形式は変更しない。
2. OpenAPI出力とaspida生成を整備し、useAspidaSWRでReactへ接続する。
3. 定義座標とSVGの閲覧用図、階層・世代選択、予定変更を実装する。
4. 開発ホストによるViteプロセス管理、publish時ビルド、CIを整備する。
5. API・Vitest・実APIを使用したPlaywright・既存全体試験を実施する。画面イメージを保存し目視確認する。

図はJP1定義のel座標を必須とし、SVGで描画する。ELKは削除する。月間一覧は確定予定・実績の読み取り専用APIとuseAspidaSWRで実装する。

実機確認でSpaProxyは最初のアクセス前にViteを起動しなかったため、Developmentかつ開発プロファイルで有効になるHostedServiceからnpm run devを起動する。ホスト終了時は子プロセスも終了する。

実装と検証の結果は[検証記録](verification.md)に記載する。画面イメージは[仕様](spec.md)から参照する。
