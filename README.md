# Eternal League of Networking (EMP)

[![Elin Together CI Deploy](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml/badge.svg)](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml) [![GitHub tag](https://img.shields.io/github/tag/ElinTogether/ElinTogether.svg)](https://GitHub.com/ElinTogether/ElinTogether/tags/) [![.NET SDK 11.0.x](https://img.shields.io/badge/11-green?logoColor=blue&label=dotnet%20SDK&labelColor=blue)](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)


English | [中文](README_zh.md) | [日本語](README_ja.md)

A WIP attempt of bringing networking feature to [Elin](https://store.steampowered.com/app/2135150/Elin/).

## Play

Requires [YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753).

You can install this mod package via Steam Workshop (link unavailable) or the automated builds from [GitHub Releases](https://github.com/ElinTogether/ElinTogether/releases).

To play with friends, it's recommended to use a minimal modlist and keep them consistent for all players. Use Steam Workshop Collections for that purpose.

To be a host, start the game **via Steam**, load into a save or make a new game (recommended), and open up the panel from **Esc-Mods-Elin Together**.

## FAQ

### How does the turn-based world work?

Each player acts at their own speed, and the host's world continues accordingly. Player actions are concurrent and do not block one another. You can also configure a shared average speed.

### How does combat work?

On top of the fluid turn sync system, you can also enable classic turn-based combat in the config, where each player decides their actions before the world continues.

### Client players can't advance some quests.

It's intended. You may get errors as client player. The host player is the only one who can actually advance the quest.

### Client players may see ghost items that can't be interacted with.

If somehow items are out of sync, try reconnecting.

### Connection froze; not responding; can't rejoin...

Restart the game to clean up the Steam connections.

## Report Bugs

Use the [issue template here](https://github.com/ElinTogether/ElinTogether/issues/new/choose).

## Build

This project requires environment variable `ElinGamePath` set to the root folder of the Elin game installation.
```
ElinGamePath/
├─ BepInEx/
│  ├─ core/
│  │  ├─ *.dll
├─ Elin_Data/
│  ├─ Managed/
│  │  ├─ *.dll
```

This project uses [.NET SDK 11.0](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) to compile correctly.

Clone the project:
```ps
git clone https://github.com/ElinTogether/ElinTogether.git
cd ElinTogether
```

Install the deps:
```ps
dotnet restore ./ElinTogether --locked-mode
```

Build the project:
```ps
dotnet build ./ElinTogether -c Debug -o ./out --no-restore
```

## Contributing

Please explain the changes and link any related issues. Be responsible for any AI-generated codes and do not push slop without reviewing and testing.

## Credits

- [DK](https://github.com/gottyduke) - code, framework
- [Redgeioz](https://github.com/Redgeioz) - code, framework
- [105gun](https://github.com/105gun) - code
- [Han](https://github.com/chuahan) - testing, a lot of
- [Omega](https://steamcommunity.com/profiles/76561198004587603) - testing
- [InuiDame](https://github.com/InuiDame) - testing
- [Drakeny](https://github.com/Drakeny) - testing
- noa - supporting the project and modding community

---
<p align="center">MIT License, 2025-present</p>
