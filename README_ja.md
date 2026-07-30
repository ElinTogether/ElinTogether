# Eternal League of Networking (EMP)

[![Elin Together CI Deploy](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml/badge.svg)](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml) [![GitHub tag](https://img.shields.io/github/tag/ElinTogether/ElinTogether.svg)](https://GitHub.com/ElinTogether/ElinTogether/tags/) [![.NET SDK 11.0.x](https://img.shields.io/badge/11-green?logoColor=blue&label=dotnet%20SDK&labelColor=blue)](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)

[English](README.md) | [中文](README_zh.md) | 日本語

友達と一緒に [Elin](https://store.steampowered.com/app/2135150/Elin/) の世界へ——家を建てて、ネフィアに潜って、エラーポップアップも一緒に眺めましょう。

数ヶ月の開発を経て、本 MOD はパブリックベータに入りました。不具合を見つけたらぜひ報告してください。

## プレイ

[YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753) が必要です。MOD 一覧で Elin Together より上に配置してください。

[Steam ワークショップ](https://steamcommunity.com/sharedfiles/filedetails/?id=3773298709) または [GitHub Releases](https://github.com/ElinTogether/ElinTogether/releases) の自動ビルドからインストールできます。

### バージョン

ワークショップ版は常に最新の Nightly ビルドに対応しています。安定版で相性の問題が出た場合は、GitHub から Stable ビルドを入手できます。

### ホストを立てる

- **Steam から**ゲームを起動し、セーブをロードするか新規ゲームを開始（推奨）
- **Esc** → **Mods** → **Elin Together** でマルチプレイパネルを開く
- そこからホストを開始
- パネルから、または Steam のフレンドリストからプレイヤーを招待

![Elin Together パネル](https://i.postimg.cc/vHqQLbV0/Pix-Pin-2026-07-28-09-25-19.png)

フレンドと遊ぶ際は、MOD 構成をできるだけ少なく、全プレイヤーでまったく同じに揃えてください——共有にはワークショップのコレクションが便利です。

## FAQ

### 他のプレイヤーとコミュニケーションを取るにはどうすればいいですか？

マーカーを設置するには「P」キーを、チャットするには「Return」キーを押してください。

### ターン制のワールドはどう動くの？

プレイヤーはそれぞれ自分の速度で行動し、ホストのワールドがそれに合わせて進みます。行動は同時に進むので、お互いを待たせることはありません。設定で平均速度を共有することもできます。

### 戦闘はどうなるの？

このなめらかなターン同期システムに加えて、設定でクラシックなターン制戦闘も有効にできます。この場合、全員が行動を決めてからワールドが進みます。

### クライアント側でマップを移動できない

仕様です。マップを移動できるのはホストプレイヤーだけです。

### クライアント側で進まないクエストがある

仕様です。クライアントではエラーが出ることもあります。クエストを実際に進められるのはホストだけです。

### クライアント側に操作できないゴーストアイテムが見える

アイテムの同期がずれた場合は、再同期を行ってください。再同期の操作は、ホストまたはクライアントマシンのいずれかのパネルから実行できます。

### 接続がフリーズした・反応しない・入り直せない……

ゲームを再起動して、Steam の接続をクリーンアップしてください。

### <○○> MOD とは一緒に使える？

現時点では MOD の互換性サポートは行っていません。問題が発生した場合は、該当の MOD を外してお試しください。

## バグ報告・機能要望

[Issue テンプレート](https://github.com/ElinTogether/ElinTogether/issues/new/choose) をご利用ください。

ワークショップのコメント欄に書かれた報告は見ていません。

## ビルド

本プロジェクトでは、以下の環境変数を設定する必要があります。

`ElinGamePath`: Elinのインストール先ルートディレクトリを指定します。
```
ElinGamePath/
├─ BepInEx/
│  ├─ core/
│  │  ├─ *.dll
├─ Elin_Data/
│  ├─ Managed/
│  │  ├─ *.dll
```

`SteamContentPath`: `YKFramework.dll` を参照できるようにするため、`steamapps/workshop/content` ディレクトリを指定します。

このプロジェクトのコンパイルには [.NET SDK 11.0](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) が必要です。

プロジェクトをクローン：
```ps
git clone https://github.com/ElinTogether/ElinTogether.git
cd ElinTogether
```

依存関係のインストール：
```ps
dotnet restore ./ElinTogether --locked-mode
```

ビルド：
```ps
dotnet build ./ElinTogether -c Debug -o ./out --no-restore
```

## コントリビューション

変更内容を説明し、関連する Issue をリンクしてください。AI 生成コードを使用する場合は責任を持ち、未レビュー・未テストのコードをプッシュしないでください。

## クレジット

- [DK](https://github.com/gottyduke) - コード、フレームワーク
- [Redgeioz](https://github.com/Redgeioz) - コード、フレームワーク
- [105gun](https://github.com/105gun) - コード
- [Han](https://github.com/chuahan) - テスト（多数）
- [Omega](https://steamcommunity.com/profiles/76561198004587603) - テスト
- [InuiDame](https://github.com/InuiDame) - テスト
- [Drakeny](https://github.com/Drakeny) - テスト
- [Overlord](https://github.com/overlord-99) - テスト
- noa - プロジェクトと MOD コミュニティへのサポート

---
<p align="center">MIT License, 2025-present</p>
