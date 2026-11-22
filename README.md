# AnnotImg

画像に文字・矢印・図形・ぼかしを書き込むための、Windows 用のシンプルな画像注釈ツールです。
スクリーンショットへの説明書き込みや、個人情報・パスワードなどを隠す用途を想定しています。

## 主な機能

- **読み込み**: PNG / JPEG を「開く」またはウィンドウへのドラッグ＆ドロップで読み込み
- **書き込めるオブジェクト**
  - 文字列（フォント・色・サイズ、複数行可）
  - 矢印（向き・長さ・色・太さ・線種）
  - 線（色・太さ・線種：直線 / 点線 / 破線）
  - 円（線の設定に加え、塗り色と不透明度）
  - 四角形（円と同じ設定に加え、角丸）
  - ぼかし（指定範囲の文字が判読できない強さでぼかす）
- **編集**: 作成後の選択・移動・変形・削除、元に戻す / やり直し、拡大縮小
- **保存**: PNG / JPEG / WebP

書き込んだ内容は保存時に画像へ焼き込まれます。保存した画像を開き直しても、文字や図形を個別に編集し直すことはできません。

## 動作環境

- Windows 11（x64）
- 配布用の exe は .NET ランタイムを同梱しているため、別途インストールは不要です

## 使い方

1. `AnnotImg.exe` を起動します（インストール不要）
2. 画像をウィンドウにドロップするか、「開く」(Ctrl+O) で選択します
3. ツールバーでツールを選び、画像上をドラッグして作成します（文字はクリックした位置に入力）
4. 右側のパネルで色・太さなどを変更します。選択中のオブジェクトにも反映されます
5. 「保存」(Ctrl+S) で、形式（PNG / JPEG / WebP）を選んで保存します

元の画像を誤って上書きしないよう、保存は常に「名前を付けて保存」です。初期のファイル名は `元の名前_annotated.png` になります。

> **初回起動について**
> exe にはコード署名がないため、ダウンロードしたファイルを開くと「Windows によって PC が保護されました」と表示されることがあります。その場合は「詳細情報」→「実行」で起動できます。
> また、初回起動時は内部ファイルを一時フォルダーに展開するため、起動に数秒かかります。

### ショートカット

| 操作 | キー |
| --- | --- |
| 開く / 保存 | Ctrl+O / Ctrl+S |
| 元に戻す / やり直し | Ctrl+Z / Ctrl+Y（または Ctrl+Shift+Z） |
| ツール切り替え | V 選択 / T 文字 / A 矢印 / L 線 / E 円 / R 四角形 / B ぼかし |
| 削除 | Delete |
| 1px 移動（10px 移動） | 矢印キー（Shift+矢印キー） |
| 角度を 45° 単位に / 正方形・正円にする | ドラッグ中に Shift |
| 拡大 / 縮小 | Ctrl+ホイール、Ctrl++ / Ctrl+- |
| ウィンドウに合わせる / 100% 表示 | Ctrl+0 / Ctrl+1 |
| 文字の確定 | Esc / Ctrl+Enter / 文字の外をクリック |
| 文字の再編集 | 文字をダブルクリック |
| ツール解除 / 選択解除 | Esc |

図形を 1 つ作成すると、自動的に選択ツールに戻ります。

## ソースからのビルド

[.NET 11 SDK](https://dotnet.microsoft.com/download) が必要です。

```powershell
cd src

# 実行（開発用）
dotnet run

# 配布用の単一 exe を作成（.NET ランタイム同梱）→ dist\AnnotImg.exe
dotnet publish AnnotImg.csproj -c Release -o ..\dist
```

### 構成

| ファイル | 内容 |
| --- | --- |
| [src/MainWindow.xaml](src/MainWindow.xaml) / [.cs](src/MainWindow.xaml.cs) | 画面レイアウト、ツールバー、プロパティパネル、ファイル操作、キー操作 |
| [src/EditorCanvas.cs](src/EditorCanvas.cs) | 描画キャンバス（マウス操作、選択・変形、文字入力、Undo / Redo、書き出し） |
| [src/Annotation.cs](src/Annotation.cs) | オブジェクトのデータモデル |
| [src/AnnotationRenderer.cs](src/AnnotationRenderer.cs) | オブジェクトの描画とクリック判定（画面表示と保存で共通） |
| [src/ImageOps.cs](src/ImageOps.cs) | 画像の読み込み（EXIF 回転補正）、ぼかし処理、PNG / JPEG / WebP 保存 |

UI は WPF です。WebP の保存には [SkiaSharp](https://github.com/mono/SkiaSharp) を使用しています。

## ライセンス

[MIT License](LICENSE) © 2026 hyperdb

同梱しているライブラリ（SkiaSharp：MIT License、.NET ランタイム：MIT License）は、それぞれのライセンスに従います。
