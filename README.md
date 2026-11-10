# Eternal League of Networking (EMP)

[![Elin Together CI Deploy](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml/badge.svg)](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml) [![GitHub tag](https://img.shields.io/github/tag/ElinTogether/ElinTogether.svg)](https://GitHub.com/ElinTogether/ElinTogether/tags/) [![.NET SDK 11.0.x](https://img.shields.io/badge/11-green?logoColor=blue&label=dotnet%20SDK&labelColor=blue)](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)


English | [中文](README_zh.md) | [日本語](README_ja.md)

Adventure through the world of [Elin](https://store.steampowered.com/app/2135150/Elin/) with your friends — build a home, dive into nefias, and watch error popups together.

Elin Together 1.0 is out: every player now travels on their own, keeps their own quests, nights and story, and plays the world as a solo player would. Please report any bugs you run into.

## Play

Requires [YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753). Make sure it's placed above Elin Together in the mod viewer.

You can install this mod package via [Steam Workshop](https://steamcommunity.com/sharedfiles/filedetails/?id=3773298709) or from [GitHub Releases](https://github.com/ElinTogether/ElinTogether/releases): download `ElinTogether.zip`, unzip it and run `Install.bat`, which finds Elin and copies the mod into `Elin\Package\Mod_ElinTogether`.

The host and every guest must use the same version of Elin Together.

### Version

Each release is built for one version of Elin (written on the release). The automated builds on GitHub Releases come in a Nightly and a Stable flavour; if you run into compatibility issues with your game version, take the build that matches it.

### To host

- Launch the game **via Steam**, load a save or create a new game. A game can be opened to others once the world has a claimed land.
- Open the multiplayer panel (the **Multiplayer** button, or **Esc** → **Mods** → **Elin Together**) and press **Start Server** in the Lobby tab.
- Invite players with **Invite Friend**, or let them use **Join Game** on your name in the Steam friends list.
- Every new behaviour can be turned on or off in the **Server Setting** tab.

### To join

- Accept the Steam invite or use **Join Game** from the title screen.
- The first time in a world, make your character on the game's own creation screen; afterwards you get it back.
- If the game uses Workshop mods you do not have, Elin restarts once with them and brings you back into the game.

![Elin Together panel](https://i.postimg.cc/vHqQLbV0/Pix-Pin-2026-07-28-09-25-19.png)

To play with friends, keep the mod list as small as possible — the mod compares every player's list and fetches what is missing.

## FAQ

### How to communicate with other players?

You can ping by `P` key or press `Return` to chat. The chat also has drawings and a mail system between players.

### How does the turn-based world work?

Each player acts at their own speed, and the world advances accordingly. Player actions are concurrent and do not block one another. You can also configure a shared average speed.

### How does combat work?

On top of the fluid turn sync system, you can also enable classic turn-based combat in the config, where each player decides their action before the world continues.

### Can guests change map?

Yes. Every player goes where they want: a player alone on a map runs it in their own game, and players who meet on the same map play it together. The world stays one for everyone.

### Can guests advance quests?

Yes. Quests, nights, recipes, the dream and the story are each player's own; what the world needs to share (the map, the bases, the main story's characters) is kept the same for everyone.

### Players may see ghost items that can't be interacted with.

The mod repairs the map by itself when it notices a difference. A quick resync can also be started from the Elin Together panel on either side.

### Connection froze; not responding; can't rejoin...

A player whose connection drops joins the same game again by itself and gets back what it played. If the host leaves for good, another player can take the world over.

### Is this compatible with X mod?

Mods that only change the look are fine. If issues occur, try removing the mod in question first, and tell us which one it was.

## Report Bugs & Feature Requests

In the game: **Client Settings** → **Help** → **Report a bug** (it sends what you write, the last 30 seconds of your screen as a video, and the logs). Or use the [issue template here](https://github.com/ElinTogether/ElinTogether/issues/new/choose).

Reports left in the Steam Workshop comments section are ignored.

## Build

This project requires 2 environment variables:

`ElinGamePath` set to the root folder of the Elin game installation.
```
ElinGamePath/
├─ BepInEx/
│  ├─ core/
│  │  ├─ *.dll
├─ Elin_Data/
│  ├─ Managed/
│  │  ├─ *.dll
```

`SteamContentPath` set to your `steamapps/workshop/content` directory so `YKFramework.dll` can be referenced.

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
- [jeanfreger](https://github.com/jeanfreger) - code, lead dev since 1.0 (with Claude Code, an AI coding assistant, under review and testing)
- [Han](https://github.com/chuahan) - testing, a lot of
- [Omega](https://steamcommunity.com/profiles/76561198004587603) - testing
- [InuiDame](https://github.com/InuiDame) - testing
- [Drakeny](https://github.com/Drakeny) - testing
- [Overlord](https://github.com/overlord-99) - testing
- lemiwinks, nardole, 梦天冰, fartcreature - testing
- noa - supporting the project and modding community

---
<p align="center">MIT License, 2025-present</p>
