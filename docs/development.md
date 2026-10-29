# 開発・ビルドガイド

DriveScopeをソースからビルド・検証・配布する手順です。

## ソースからビルド

Windows付属のC#コンパイラとWPFを使用します。追加パッケージの復元は不要です。ビルド前に、このリポジトリのフォルダから起動したDriveScopeを終了してください。

```powershell
git clone https://github.com/bassmicrobe/DriveScope.git
cd DriveScope
.\build.ps1
Start-Process .\DriveScope.exe
```

生成される `DriveScope.exe` はリポジトリのルートに置かれます。実行時には同じフォルダの `DriveScope.exe.config` も使用します。

## 配布ZIPを作成

```powershell
.\package.ps1
```

ビルドとセルフテストを実行し、成功すると `dist/DriveScope-portable.zip` を更新します。ZIPにはexe、設定ファイル、README、LICENSEとREADME表示用のロゴ・画面画像を含めます。アプリ用のアイコンと画面内ロゴはexeに埋め込んでいるため、外部の画像ファイルがなくても起動できます。ソースを変更して配布するときは、ZIPもこのコマンドで更新してください。

## 検証

セルフテストは小さなテスト用ファイルを作成し、分類、バイト数の集計、長いパス、中断、結果の保存、CSV、ドライブの追加・取り外し、選択内容の保存などを確認します。ドライブ全体のスキャンは行いません。

```powershell
# package.ps1からも実行されるセルフテスト
$reportDirectory = Join-Path $PWD 'artifacts\verification'
$testProcess = Start-Process .\DriveScope.exe -ArgumentList @(
    '--self-test', ('"' + $reportDirectory + '"')
) -WindowStyle Hidden -Wait -PassThru
$testProcess.ExitCode
Get-Content (Join-Path $reportDirectory 'self-test.txt')
```

終了コード `0` が成功です。検証用の生成物は `artifacts` に保存し、Git管理の対象から除外しています。

保存済みの実ドライブの結果を使った画面検証もできます。あらかじめアプリでスキャンしてから終了し、次を実行してください。

```powershell
Start-Process .\DriveScope.exe -ArgumentList @(
    '--verify-ui', ('"' + $reportDirectory + '"')
) -WindowStyle Hidden -Wait
Get-Content (Join-Path $reportDirectory 'ui-test.txt')
```

画面検証のレポートや画像には実際のPCのドライブ情報・ファイルパスが含まれるため、配布ZIPには含めていません。

実ドライブや保存済みの設定を使わず、サンプルデータでキーボード操作、絞り込み、小さいウィンドウ、定期更新時の表示維持を確認するには、次のコマンドを使用します。

```powershell
Start-Process .\DriveScope.exe -ArgumentList @(
    '--verify-presentation', ('"' + $reportDirectory + '"')
) -WindowStyle Hidden -Wait
Get-Content (Join-Path $reportDirectory 'presentation-test.txt')
Get-Content (Join-Path $reportDirectory 'idle\refresh-test.txt')
```

周期更新の回帰テストは `--verify-refresh` で実行できます。約10秒間、自動更新中に一覧・行の選択・背景ブラシが作り直されないことを確認し、その後に値の更新が既存のセルへ反映されることを検証します。

```powershell
Start-Process .\DriveScope.exe -ArgumentList @(
    '--verify-refresh', ('"' + $reportDirectory + '"')
) -WindowStyle Hidden -Wait
Get-Content (Join-Path $reportDirectory 'refresh-test.txt')
```

READMEの画面画像は、アプリを終了した状態で次のコマンドから再生成できます。このモードでは実ドライブや保存済みの集計結果・選択設定を読み込まず、サンプルデータだけを表示します。

```powershell
Start-Process .\DriveScope.exe -ArgumentList @(
    '--docs-screenshot', ('"' + (Join-Path $PWD 'assets\screenshots\drivescope-overview.png') + '"')
) -WindowStyle Hidden -Wait
```

## ブランディング素材

アプリアイコンにはダーク版、画面ヘッダーにはLOCAL付きの横長ロゴを使用しています。READMEの冒頭にはロゴ入りのOGP画像を掲載しています。元のSVGは `assets/branding` に保存しています。派生画像を再生成する場合のみ、Node.jsと`sharp`を導入した開発環境で `node tools/render-branding.cjs` を実行してください。通常のビルドやアプリの実行には不要です。

## 変更時の注意

変更時は `package.ps1` による検証を行い、UIを変更した場合は画面表示も確認してください。不具合の報告にはWindowsのバージョン、ドライブの種類、再現手順を添えると調査しやすくなります。
