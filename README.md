# Eternal League of Networking (EMP)

[![Elin Together CI Deploy](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml/badge.svg)](https://github.com/ElinTogether/ElinTogether/actions/workflows/emp_ci.yml) [![GitHub tag](https://img.shields.io/github/tag/ElinTogether/ElinTogether.svg)](https://GitHub.com/ElinTogether/ElinTogether/tags/) [![.NET SDK 11.0.x](https://img.shields.io/badge/11-green?logoColor=blue&label=dotnet%20SDK&labelColor=blue)](https://dotnet.microsoft.com/en-us/download/dotnet/11.0)


A WIP attempt of bringing networking feature to [Elin](https://store.steampowered.com/app/2135150/Elin/).

## Play

Requires [YK Framework](https://steamcommunity.com/sharedfiles/filedetails/?id=3400020753).

You can install this mod package via Steam Workshop (link unavailable) or the automated builds from [GitHub Releases](https://github.com/ElinTogether/ElinTogether/releases).

To play with friends, it's recommended to use a minimal modlist and keep them consistent for all players. Use Steam Workshop Collections for that purpose.

To be a host, start the game, load into a save or make a new game (recommended), and open up the panel from **Esc-Mods-Elin Together**.

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
