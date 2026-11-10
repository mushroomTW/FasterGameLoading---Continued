# Faster Game Loading - Continued

[![RimWorld 1.6 | 1.7](https://img.shields.io/badge/RimWorld-1.6%20%7C%201.7-brightgreen.svg)](http://rimworldgame.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Languages](https://img.shields.io/badge/languages-EN%20%7C%20Traditional%20Chinese%20%7C%20Simplified%20Chinese%20%7C%20RU-orange.svg)

English | [繁體中文](README.zh-TW.md)

Makes RimWorld reach the main menu faster on large mod lists. This mod reduces startup costs from XML loading, reflection, textures, and atlas work without changing gameplay or save data.

Original mod by [Taranchuk](https://github.com/Taranchuk/FasterGameLoading); this is a maintained fork with compatibility fixes and performance improvements.

## Versions

RimWorld 1.6 and 1.7 run on different engines, so each has its own build with its own documentation:

| Game version | Runtime | Documentation |
| --- | --- | --- |
| RimWorld 1.6 | Unity / Mono (.NET Framework 4.7.2) | [1.6 README](1.6/README.md) |
| RimWorld 1.7 | Waylith / .NET 10 | [1.7 README](1.7/README.md) |

Each README covers that build's startup flow, features, measurements, compatibility, recommended settings and how to build it. RimWorld loads the matching folder automatically; you do not need to choose a version.

## Installation

> [!IMPORTANT]
> Requires RimWorld 1.6 or 1.7 and [Harmony](https://github.com/pardeike/HarmonyRimWorld/releases/latest). Load this mod after Harmony.

- **Steam Workshop**: subscribe on the [Workshop page](https://steamcommunity.com/sharedfiles/filedetails/?id=3652938473).
- **Manual**: download or clone this repository into your `RimWorld/Mods/` folder, then enable **Faster Game Loading - Continued** in the in-game mod list.

All options live in `Options → Mod options → Faster Game Loading - Continued`. Most players can keep the defaults; see Recommended Settings in the README for your game version.

## Project Structure

```text
FasterGameLoading/
├── About/                         # Mod metadata, preview, and Workshop id
├── 1.6/                           # RimWorld 1.6 (.NET Framework 4.7.2)
│   ├── Assemblies/                # Compiled DLL
│   ├── LanguageData/              # Translation XML files (EN, zh-TW, zh-CN, RU)
│   ├── Source/                    # Source for 1.6
│   ├── README.md                  # Documentation for 1.6 (English)
│   └── README.zh-TW.md            # Documentation for 1.6 (Traditional Chinese)
├── 1.7/                           # RimWorld 1.7 (.NET 10)
│   ├── Assemblies/                # Compiled DLL
│   ├── LanguageData/              # Translation XML files (EN, zh-TW, zh-CN, RU)
│   ├── Source/                    # Source for 1.7 (independently maintained)
│   ├── README.md                  # Documentation for 1.7 (English)
│   └── README.zh-TW.md            # Documentation for 1.7 (Traditional Chinese)
├── SteamDescriptions/             # Steam Workshop description sources
├── LICENSE
├── README.md
└── README.zh-TW.md
```

RimWorld picks `1.6/` or `1.7/` by the running version, so each build writes its DLL to the `Assemblies/` folder next to its `Source/`. Build and test commands are in each version's README.

Each build loads only its own `LanguageData/` folder; the 1.7 translations omit delaying graphic and icon loading, adaptive atlas baking, and faster atlas compression, which are only available in 1.6.

## Credits and License

Original mod by [Taranchuk](https://github.com/Taranchuk/FasterGameLoading). This fork is maintained by [mushroomTW](https://github.com/mushroomTW/FasterGameLoading---Continued) with compatibility fixes and performance improvements. Licensed under the [MIT License](LICENSE).
