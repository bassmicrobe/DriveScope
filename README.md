![DriveScope — ドライブ容量を、ひと目で。カテゴリ別に可視化・比較](assets/social/drivescope-ogp.jpg)

# DriveScope

接続中のドライブを自動検出し、**何に容量を使っているかをカテゴリ別に比較できるWindowsアプリ**です。

## 概要

調べたいドライブにチェックを付けるだけで、フォルダの内訳や大きいファイルを確認できます。Unity / VRChatのプロジェクト、ゲーム、キャッシュ、AIモデルなど13種類のカテゴリに分類します。インストール不要で、ファイルの削除・移動機能や外部送信機能はありません。

![ロゴを適用したDriveScopeのドライブ比較画面](assets/screenshots/drivescope-overview.png)

ロゴ適用後のアプリから撮影した表示例です。容量・ドライブ名はサンプルデータで、実際のPCの情報は含みません。チェックしたドライブをカテゴリごとに横並びで比較できます。

| 機能 | 内容 |
| --- | --- |
| ドライブ選択 | ローカル・リムーバブルドライブなどから対象を選び、選択内容を保存 |
| 接続・取り外しの検出 | 約3秒ごとに一覧を更新。手動の「ドライブ更新」にも対応 |
| カテゴリ比較 | 選択したドライブを横並びで表示。台数に応じて列が増減 |
| フォルダの内訳 | 子孫を含めたサイズを表示し、ダブルクリックで掘り下げ |
| 大きいファイル | 容量順の一覧から、エクスプローラーで保存場所を確認 |
| 中断・結果の保存 | 途中結果を保存し、次回起動時に表示 |
| CSV出力 | Excelで開けるUTF-8 BOM付きCSVとしてカテゴリ・フォルダ・ファイルを出力 |

## 技術スタック

- C# / WPF（.NET Framework 4.8）
- Windows付属のC#コンパイラでビルド（追加パッケージの復元は不要）
- PowerShellスクリプトによるビルド・検証・配布（`build.ps1` / `package.ps1`）
- ロゴ・アイコンの再生成のみNode.js + `sharp` を使用（通常のビルド・実行には不要）

## セットアップ

**[DriveScope-portable.zip をダウンロード](https://github.com/bassmicrobe/DriveScope/releases/latest/download/DriveScope-portable.zip)**

最新版は[GitHub Releases](https://github.com/bassmicrobe/DriveScope/releases/latest)から取得できます。リリースページでは変更内容も確認できます。

1. ZIPを任意のフォルダに解凍します。
2. `DriveScope.exe` を起動します。`DriveScope.exe.config` はexeと同じフォルダに置いてください。
3. 調べたいドライブの「対象」にチェックを付け、「チェックしたドライブをスキャン」を押します。

別のPCでも同じZIPを解凍して利用できます。移動先のドライブを自動検出し、設定や集計結果はそのPCに保存します。ZIPには集計結果や個人設定を含めていません。

### 動作環境

- Windows 10 / 11の64ビット版
- .NET Framework 4.8

.NET SDK、Python、Node.jsのインストールは不要です。

### ソースからビルド

```powershell
git clone https://github.com/bassmicrobe/DriveScope.git
cd DriveScope
.\build.ps1
Start-Process .\DriveScope.exe
```

配布ZIPの作成や検証コマンドの詳細は[開発・ビルドガイド](docs/development.md)を参照してください。

## 使い方

- **ドライブを比較する:** 起動画面の「ドライブ比較」で、チェックしたドライブの内訳を確認します。
- **詳細を見る:** ドライブのカードや左のカテゴリをクリックします。フォルダをダブルクリックすると中へ進み、「↑ 上へ」で戻れます。
- **保存場所を開く:** フォルダ・ファイルを選び「エクスプローラーで表示」を押します。
- **最新の状態に更新する:** 対象を再スキャンします。起動時に表示される前回の結果は、ファイルの変更に自動追従しません。
- **途中で止める:** 「中断」を押します。ウィンドウを閉じる場合も、走査を中断して途中結果を保存します。

初回スキャンはファイル数により数分以上かかります。走査中も途中集計を確認できます。ドライブの追加・取り外し時の挙動、容量の算出方法と制限、保存先の詳細は[使い方の詳細](docs/usage.md)を参照してください。

## 構成

```text
assets/branding/           元のSVGロゴ・派生PNG・Windows用ICO
assets/screenshots/        README用のアプリ画面（サンプルデータ）
docs/                      使い方・開発の詳細ドキュメント
src/
  App.cs                 画面操作・集計表示
  MainWindow.xaml        WPFの画面定義
  DriveManagement.cs     ドライブ検出・識別・選択保存
  Scanner.cs             ファイル走査・分類・集計・結果保存
  Verification.cs        セルフテストと画面検証
  ViewRows.cs            行の差分更新・変更通知
  PresentationVerification.cs  表示更新の回帰テスト・公開用サンプル表示
  app.manifest           実行権限・長いパスへの対応
DriveScope.exe.config    .NET Frameworkの実行設定
build.ps1                ビルド
package.ps1              ビルド・テスト・ZIP作成
tools/render-branding.cjs  SVGからPNG・複数サイズのICOを再生成
dist/                    配布用ZIP
```

## ドキュメント

- [使い方の詳細](docs/usage.md) — 操作方法、カテゴリ分類、ドライブの追加・取り外し、容量の算出方法と制限、保存先
- [開発・ビルドガイド](docs/development.md) — ソースからのビルド、配布ZIPの作成、セルフテスト・画面検証、ブランディング素材

## ライセンス

[MIT License](LICENSE)
