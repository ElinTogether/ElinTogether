# Eternal League of Networking (EMP)

[![Elin Together CI Deploy](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml/badge.svg)](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml) [![GitHub tag](https://img.shields.io/github/tag/ElinTogether/ElinTogether.svg)](https://GitHub.com/ElinTogether/ElinTogether/tags/) [![.NET SDK 11.0.x](https://img.shields.io/badge/11-green?logoColor=blue&label=dotnet%20SDK&labelColor=blue)](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)

[English](README.md) | 中文 | [日本語](README_ja.md)

和朋友一起闯荡 [Elin](https://store.steampowered.com/app/2135150/Elin/) 的世界——一起建家、一起下地城、一起看红字报错弹窗。

Elin Together 1.0 已发布：每位玩家都可以自己旅行，拥有自己的任务、夜晚和剧情，像单人游戏一样游玩这个世界。如有 bug 请反馈。

## 游玩

需要安装 [YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753)，并确保它排在 Elin Together 上面。

你可以通过 [Steam 创意工坊](https://steamcommunity.com/sharedfiles/filedetails/?id=3773298709) 或 [GitHub Releases](https://github.com/ElinTogether/ElinTogether/releases) 安装此模组：下载 `ElinTogether.zip`，解压后运行 `Install.bat`，它会找到 Elin 并把模组复制到 `Elin\Package\Mod_ElinTogether`。

主机和所有客人必须使用相同版本的 Elin Together。

### 版本

每个版本都针对某一个 Elin 版本构建（写在版本说明中）。GitHub Releases 上的自动构建分为 Nightly 和 Stable；如果遇到与你的游戏版本的兼容问题，请选择对应的构建。

### 开主机

- 通过 **Steam** 启动游戏，加载存档或开新档。世界中有已认领的土地后，才能向他人开放。
- 打开联机面板（**Multiplayer** 按钮，或 **Esc** → **Mods** → **Elin Together**），在 Lobby 页面点击 **Start Server**。
- 用 **Invite Friend** 邀请玩家，或让他们在 Steam 好友列表中你的名字上使用 **加入游戏**。
- 每项新功能都可以在 **Server Setting** 页面中开启或关闭。

### 加入

- 接受 Steam 邀请，或在标题画面使用 **加入游戏**。
- 第一次进入某个世界时，在游戏自带的角色创建画面创建角色；之后会直接取回该角色。
- 如果对方使用了你没有的创意工坊模组，Elin 会带着这些模组重启一次，然后把你带回游戏。

![Elin Together 面板](https://i.postimg.cc/vHqQLbV0/Pix-Pin-2026-07-28-09-25-19.png)

与好友联机时，建议使用尽量少的模组——本模组会比较每位玩家的模组列表，并获取缺少的模组。

## FAQ

### 如何与其他玩家交流？

按 `P` 键可以标记位置，按 `Return` 键可以聊天。聊天中还可以画画，玩家之间也有邮件系统。

### 回合制的世界是怎么运作的？

每位玩家按照自己的速度行动，世界随之推进。玩家的行动是同时进行的，互不阻塞。你也可以设置一个共享的平均速度。

### 战斗怎么打？

在流畅的回合同步之外，你也可以在设置中开启经典回合制战斗：每位玩家决定好行动后世界才会继续。

### 客人可以切换地图吗？

可以。每位玩家都可以去自己想去的地方：独自在某张地图上的玩家由自己的游戏运行这张地图，在同一张地图相遇的玩家一起游玩。对所有人来说，世界仍然是同一个。

### 客人可以推进任务吗？

可以。任务、夜晚、配方、梦境和剧情都是每位玩家自己的；世界需要共享的内容（地图、据点、主线剧情的角色）对所有人保持一致。

### 玩家看到没法互动的幽灵物品

本模组在发现不一致时会自动修复地图。也可以在任意一方的 Elin Together 面板中发起快速重新同步。

### 连接卡住、没反应、进不去……

断线的玩家会自动重新加入同一个游戏，并取回自己玩过的内容。如果主机彻底离开，其他玩家可以接管这个世界。

### 和 <某某> 模组兼容吗？

只改变外观的模组没有问题。如果出现问题，请先尝试移除相关模组，并告诉我们是哪一个。

## 提交 Bug 和新功能

在游戏中：**Client Settings** → **Help** → **Report a bug**（会发送你写的内容、屏幕最后 30 秒的视频和日志）。或者使用[这里的 issue 模板](https://github.com/ElinTogether/ElinTogether/issues/new/choose)。

创意工坊评论区里的反馈不会被处理。

## 构建

此项目需要设置如下环境变量:

`ElinGamePath`，指向 Elin 游戏安装的根目录。
```
ElinGamePath/
├─ BepInEx/
│  ├─ core/
│  │  ├─ *.dll
├─ Elin_Data/
│  ├─ Managed/
│  │  ├─ *.dll
```

`SteamContentPath`，指向 `steamapps/workshop/content` 目录，以便能够引用 `YKFramework.dll`。

此项目使用 [.NET SDK 11.0](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) 进行编译。

克隆项目：
```ps
git clone https://github.com/ElinTogether/ElinTogether.git
cd ElinTogether
```

安装依赖：
```ps
dotnet restore ./ElinTogether --locked-mode
```

构建项目：
```ps
dotnet build ./ElinTogether -c Debug -o ./out --no-restore
```

## 贡献

请说明你的修改内容，并关联相关的 issue。如使用 AI 生成的代码，请对其负责，未经审查和测试的代码请勿提交。

## 致谢

- [DK](https://github.com/gottyduke) - 代码、框架
- [Redgeioz](https://github.com/Redgeioz) - 代码、框架
- [105gun](https://github.com/105gun) - 代码
- [jeanfreger](https://github.com/jeanfreger) - 代码，1.0 起的主要开发者（借助 AI 编程助手 Claude Code，经过审查和测试）
- [Han](https://github.com/chuahan) - 大量测试
- [Omega](https://steamcommunity.com/profiles/76561198004587603) - 测试
- [InuiDame](https://github.com/InuiDame) - 测试
- [Drakeny](https://github.com/Drakeny) - 测试
- [Overlord](https://github.com/overlord-99) - 测试
- lemiwinks、nardole、梦天冰、fartcreature - 测试
- noa - 支持着项目和模组社区

---
<p align="center">MIT License, 2025-present</p>
