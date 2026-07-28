# Eternal League of Networking (EMP)

[![Elin Together CI Deploy](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml/badge.svg)](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml) [![GitHub tag](https://img.shields.io/github/tag/ElinTogether/ElinTogether.svg)](https://GitHub.com/ElinTogether/ElinTogether/tags/) [![.NET SDK 11.0.x](https://img.shields.io/badge/11-green?logoColor=blue&label=dotnet%20SDK&labelColor=blue)](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)

[English](README.md) | 中文 | [日本語](README_ja.md)

和朋友一起闯荡 [Elin](https://store.steampowered.com/app/2135150/Elin/) 的世界——一起建家、一起下地城、一起看红字报错弹窗。

经过数月的开发，本 Mod 目前进入公开测试阶段，如有 bug 请反馈。

## 游玩

需要安装 [YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753)，并确保它排在 Elin Together 上面。

你可以通过 Steam 创意工坊（链接暂未创建）或 [GitHub Releases](https://github.com/ElinTogether/ElinTogether/releases) 的自动构建版本安装此模组。

### 版本

创意工坊上的版本始终适配夜间版 Nightly 构建；如果遇到稳定版兼容问题，也可以去 GitHub 下载 Stable 版本。

### 开主机

- 通过 **Steam** 启动游戏，加载存档或开新档（推荐）
- 按 **Esc** → **Mods** → **Elin Together** 打开联机面板
- 在面板里开启主机
- 在面板里邀请玩家，或者直接用 Steam 好友列表

![Elin Together 面板](https://i.postimg.cc/vHqQLbV0/Pix-Pin-2026-07-28-09-25-19.png)

与好友联机时，建议使用最少的模组列表，并确保所有玩家保持一致。推荐使用 Steam 创意工坊合集来分享。

## FAQ

### 如何与其他玩家交流？

你可以按 `P` 键发送标记，或者按 `Return` 键聊天。

### 回合制的世界是怎么运作的？

每位玩家按自己的速度行动，主机世界会相应推进。玩家行动可以同时进行，不会互相阻塞。你也可以在设置中配置共享平均速度。

### 战斗怎么打？

在流畅的回合同步系统之上，你还可以在设置中开启经典回合制战斗，每位玩家决定行动后世界才会继续推进。

### 客机玩家无法切换地图

这是预期行为。只有主机玩家可以切换地图。

### 客机玩家无法推进某些任务

这是预期行为。作为客户端玩家你可能会遇到错误。只有主机玩家才能实际推进任务。

### 客机玩家看到没法互动的幽灵物品

如果物品出现不同步，请尝试重新同步，主机或客机均可使用面板进行快速重新同步操作。

### 连接卡住、没反应、进不去……

重启游戏以清理 Steam 连接。

### 和 <某某> 模组兼容吗？

目前我们不提供模组兼容性方面的支持。遇到问题请尝试移除相关模组。

## 提交 Bug 和新功能

请使用[问题模板](https://github.com/ElinTogether/ElinTogether/issues/new/choose)提交。

发在创意工坊评论区的错误反馈不会处理。

模组制作相关讨论可以加入 Elin 模组讨论群 872068953。

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
- [Han](https://github.com/chuahan) - 大量测试
- [Omega](https://steamcommunity.com/profiles/76561198004587603) - 测试
- [InuiDame](https://github.com/InuiDame) - 测试
- [Drakeny](https://github.com/Drakeny) - 测试
- [Overlord](https://github.com/overlord-99) - 测试
- noa - 支持着项目和模组社区

---
<p align="center">MIT License, 2025-present</p>
