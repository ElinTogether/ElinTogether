# Eternal League of Networking (EMP)

[![Elin Together CI Deploy](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml/badge.svg)](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml) [![GitHub tag](https://img.shields.io/github/tag/ElinTogether/ElinTogether.svg)](https://GitHub.com/ElinTogether/ElinTogether/tags/) [![.NET SDK 11.0.x](https://img.shields.io/badge/11-green?logoColor=blue&label=dotnet%20SDK&labelColor=blue)](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)

[English](README.md) | 中文 | [日本語](README_ja.md)

为 [Elin](https://store.steampowered.com/app/2135150/Elin/) 添加联机功能。

## 游玩

需要安装 [YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753)。

你可以通过 Steam 创意工坊（链接暂未创建）或 [GitHub Releases](https://github.com/ElinTogether/ElinTogether/releases) 的自动构建版本安装此模组。

与好友联机时，建议使用最少的模组列表，并确保所有玩家保持一致。推荐使用 Steam 创意工坊合集来分享。

通过 **Steam** 启动游戏，加载存档或创建新游戏（推荐），然后通过 **Esc-模组-Elin Together** 打开面板作为主机。

## FAQ

### 回合制世界如何运作？

每位玩家按自己的速度行动，主机世界会相应推进。玩家行动可以同时进行，不会互相阻塞。你也可以在设置中配置共享平均速度。

### 战斗如何运作？

在流畅的回合同步系统之上，你还可以在设置中开启经典回合制战斗，每位玩家决定行动后世界才会继续推进。

### 客户端玩家无法推进某些任务

这是预期行为。作为客户端玩家你可能会遇到错误。只有主机玩家才能实际推进任务。

### 客户端玩家可能会看到无法互动的幽灵物品

如果物品出现不同步，请尝试重新连接。

### 连接卡死、无响应、无法重新加入……

重启游戏以清理 Steam 连接。

## 报告 Bug

请使用[问题模板](https://github.com/ElinTogether/ElinTogether/issues/new/choose)提交。

## 构建

此项目需要设置环境变量 `ElinGamePath`，指向 Elin 游戏安装的根目录。
```
ElinGamePath/
├─ BepInEx/
│  ├─ core/
│  │  ├─ *.dll
├─ Elin_Data/
│  ├─ Managed/
│  │  ├─ *.dll
```

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
- noa - 支持着项目和模组社区

---
<p align="center">MIT License, 2025-present</p>
