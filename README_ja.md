# Eternal League of Networking (EMP)

[![Elin Together CI Deploy](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml/badge.svg)](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml) [![GitHub tag](https://img.shields.io/github/tag/ElinTogether/ElinTogether.svg)](https://GitHub.com/ElinTogether/ElinTogether/tags/) [![.NET SDK 11.0.x](https://img.shields.io/badge/11-green?logoColor=blue&label=dotnet%20SDK&labelColor=blue)](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)

[English](README.md) | [中文](README_zh.md) | 日本語

友達と一緒に [Elin](https://store.steampowered.com/app/2135150/Elin/) の世界へ——家を建てて、ネフィアに潜って、エラーポップアップも一緒に眺めましょう。

Elin Together 1.0 をリリースしました：プレイヤーはそれぞれ自分で旅をし、自分のクエスト、夜、ストーリーを持ち、一人で遊ぶときと同じように世界を遊べます。不具合を見つけたらぜひ報告してください。

## プレイ

[YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753) が必要です。MOD 一覧で Elin Together より上に配置してください。

[Steam ワークショップ](https://steamcommunity.com/sharedfiles/filedetails/?id=3773298709) または [GitHub Releases](https://github.com/ElinTogether/ElinTogether/releases) からインストールできます：`ElinTogether.zip` をダウンロードして展開し、`Install.bat` を実行すると、Elin を見つけて MOD を `Elin\Package\Mod_ElinTogether` にコピーします。

ホストとすべてのゲストは同じバージョンの Elin Together を使う必要があります。

### バージョン

各リリースは Elin の特定のバージョン向けにビルドされています（リリースに記載）。GitHub Releases の自動ビルドには Nightly と Stable があります。ゲームのバージョンとの相性の問題が出た場合は、合うほうを使ってください。

### ホストになる

- **Steam から**ゲームを起動し、セーブを読み込むか新しく始めます。世界に土地を取得してから、他の人に開放できます。
- マルチプレイパネル（**Multiplayer** ボタン、または **Esc** → **Mods** → **Elin Together**）を開き、Lobby タブで **Start Server** を押します。
- **Invite Friend** で招待するか、Steam のフレンドリストであなたの名前から **ゲームに参加** してもらいます。
- 新しい機能はすべて **Server Setting** タブでオン・オフできます。

### 参加する

- Steam の招待を受けるか、タイトル画面から **ゲームに参加** します。
- その世界に初めて入るときは、ゲームのキャラクター作成画面でキャラクターを作ります。次からはそのキャラクターで戻れます。
- 相手が持っていないワークショップ MOD を使っている場合、Elin はその MOD を入れて一度だけ再起動し、ゲームに戻ります。

![Elin Together パネル](https://i.postimg.cc/vHqQLbV0/Pix-Pin-2026-07-28-09-25-19.png)

友達と遊ぶときは MOD をできるだけ少なくしてください——本 MOD は全員の MOD 一覧を比べ、足りないものを取得します。

## FAQ

### 他のプレイヤーとどうやって会話する？

`P` キーでピン、`Return` キーでチャットできます。チャットでは絵も描け、プレイヤー同士のメールもあります。

### ターン制の世界はどう動いている？

各プレイヤーは自分のペースで行動し、世界はそれに合わせて進みます。プレイヤーの行動は同時に処理され、互いを妨げません。共通の平均速度を設定することもできます。

### 戦闘はどうなっている？

なめらかなターン同期に加えて、設定で従来のターン制戦闘を有効にできます。全員が行動を決めてから世界が進みます。

### ゲストはマップを移動できる？

できます。誰でも行きたい場所へ行けます：一人でいるマップはそのプレイヤーのゲームが動かし、同じマップで出会ったプレイヤーは一緒に遊びます。世界は全員にとって一つのままです。

### ゲストはクエストを進められる？

できます。クエスト、夜、レシピ、夢、ストーリーは各プレイヤーのものです。世界が共有すべきもの（マップ、拠点、メインストーリーのキャラクター）は全員で同じに保たれます。

### 触れない幽霊アイテムが見える

本 MOD は違いに気づくとマップを自動で修復します。どちらの側からでも Elin Together パネルで再同期を始められます。

### 接続が固まった・応答しない・再参加できない……

接続が切れたプレイヤーは自動で同じゲームに再参加し、遊んだ内容も戻ります。ホストが完全に抜けた場合は、他のプレイヤーが世界を引き継げます。

### ○○の MOD と互換性はある？

見た目だけを変える MOD は問題ありません。問題が起きたら、まずその MOD を外してみて、どの MOD だったか教えてください。

## バグ報告・機能要望

ゲーム内から：**Client Settings** → **Help** → **Report a bug**（書いた内容、画面の直近 30 秒の動画、ログを送ります）。または [Issue テンプレート](https://github.com/ElinTogether/ElinTogether/issues/new/choose) をご利用ください。

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
- [jeanfreger](https://github.com/jeanfreger) - コード、1.0 からのリード開発者（AI コーディングアシスタント Claude Code を使用、レビューとテスト済み）
- [Han](https://github.com/chuahan) - テスト（多数）
- [Omega](https://steamcommunity.com/profiles/76561198004587603) - テスト
- [InuiDame](https://github.com/InuiDame) - テスト
- [Drakeny](https://github.com/Drakeny) - テスト
- [Overlord](https://github.com/overlord-99) - テスト
- lemiwinks、nardole、梦天冰、fartcreature - テスト
- noa - プロジェクトと MOD コミュニティへのサポート

---
<p align="center">MIT License, 2025-present</p>
