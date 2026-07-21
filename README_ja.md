# Eternal League of Networking (EMP)

[![Elin Together CI Deploy](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml/badge.svg)](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml) [![GitHub tag](https://img.shields.io/github/tag/ElinTogether/ElinTogether.svg)](https://GitHub.com/ElinTogether/ElinTogether/tags/) [![.NET SDK 11.0.x](https://img.shields.io/badge/11-green?logoColor=blue&label=dotnet%20SDK&labelColor=blue)](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)

[English](README.md) | [中文](README_zh.md) | 日本語

[Elin](https://store.steampowered.com/app/2135150/Elin/) にオンライン機能を追加する MOD です。

## プレイ

[YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753) が必要です。

Steam ワークショップ（リンク未作成）または [GitHub Releases](https://github.com/ElinTogether/ElinTogether/releases) の自動ビルドからインストールできます。

フレンドとプレイする際は、最小限の MOD 構成を全プレイヤーで統一してください。Steam ワークショップコレクションの活用を推奨します。

ホストになるには、**Steam から**ゲームを起動し、セーブデータをロードするか新規ゲームを作成し（推奨）、**Esc - Mod - Elin Together** からパネルを開いてください。

## FAQ

### ターンベースのワールドはどのように動作しますか？

各プレイヤーは自身の速度で行動し、ホストのワールドはそれに応じて進行します。プレイヤーの行動は同時並行で行われ、他のプレイヤーをブロックしません。設定で平均速度を共有することも可能です。

### 戦闘はどのように動作しますか？

スムーズなターン同期システムに加えて、設定でクラシックなターン制戦闘を有効にすることもできます。このモードでは、全プレイヤーが行動を決定してからワールドが進行します。

### クライアント側のプレイヤーが一部のクエストを進行できない

仕様です。クライアント側ではエラーが表示されることがあります。実際にクエストを進行できるのはホストプレイヤーのみです。

### クライアント側で操作できないゴーストアイテムが表示されることがある

アイテムが同期されていない場合は、再接続をお試しください。

### 接続がフリーズした・応答しない・再参加できない場合

ゲームを再起動して Steam 接続をクリーンアップしてください。

## バグ報告

[Issue テンプレート](https://github.com/ElinTogether/ElinTogether/issues/new/choose) をご利用ください。

## ビルド

このプロジェクトでは、環境変数 `ElinGamePath` を Elin ゲームのインストールルートフォルダに設定する必要があります。
```
ElinGamePath/
├─ BepInEx/
│  ├─ core/
│  │  ├─ *.dll
├─ Elin_Data/
│  ├─ Managed/
│  │  ├─ *.dll
```

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
- noa - プロジェクトと MOD コミュニティへのサポート

---
<p align="center">MIT License, 2025-present</p>
