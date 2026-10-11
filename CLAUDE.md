# chuo

優先言語を日本語とします。
会話、コメント、コミットメッセージ等コミュニケーション用の言語は常に日本語を使用してください。

## プロジェクトの概要

- chuo はクロスプラットフォームのランチャーアプリで、プラグインで拡張できる。要件は `docs/requirements.md` にある。
- 個人で使うアプリであり、作りやすさと保守のしやすさを優先する。常駐メモリの小ささと、ホットキーを押してから表示されるまでの速さを重視する。
- 設計判断は `docs/requirements.md` と矛盾させない。

## 経緯（蒸し返さないための記録）

- 要件定義は 2026-10-05〜07 に行った。
- 最初は Tauri と deno_core（JS/TS のプラグイン）で進めかけたが、.NET と Avalonia（アセンブリのプラグイン）に切り替えた。
- GNOME の Wayland への対応は「挑戦」であり、難しければ諦めてよい。
- 2026-10-10 に、設定はすべて設定画面で変更できる方針に変えた（ueli や Flow Launcher と同様）。当初は YAML の設定ファイルを手で編集し、GUI の設定画面は2版以降としていた。これに合わせて設定ファイルは JSON にし、プラグインの設定画面はホストが Options クラスから自動で生成する。

## 現在の作業

- 技術検証（要件定義書の14章）の結果は `docs/spike-results.md` にある。検証用のコードは `spikes/` に置いてあり、本実装からは参照しない。
- 2026-10-11 に本実装を始めた。実装の状況と残りの作業は `docs/implementation.md` に記録する。

## ビルドとテスト

- dotnet は mise で入れている（`mise exec -- dotnet ...`）。
- ビルド: `dotnet build Chuo.slnx`。本体（`src/Chuo.App`）をビルドすると、同梱プラグインが出力先の `plugins/` に発行される。
- テスト: `dotnet test --project tests/Chuo.Core.Tests`（Microsoft Testing Platform を使う。`global.json` を参照）。
- Windows 向けの発行: `dotnet publish src/Chuo.App -c Release -r win-x64 --self-contained -o artifacts/publish-win`
- パッケージのバージョンは `Directory.Packages.props` で一元管理する（`spikes/` は対象外）。
