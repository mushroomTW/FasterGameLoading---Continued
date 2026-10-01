# Faster Game Loading - Continued

[![RimWorld 1.6](https://img.shields.io/badge/RimWorld-1.6-brightgreen.svg)](http://rimworldgame.com/)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![Languages](https://img.shields.io/badge/languages-EN%20%7C%20Traditional%20Chinese%20%7C%20Simplified%20Chinese%20%7C%20RU-orange.svg)

Makes RimWorld reach the main menu faster on large mod lists. This mod reduces startup costs from XML loading, reflection, textures, and atlas work without changing gameplay or save data.

Original mod by [Taranchuk](https://github.com/Taranchuk/FasterGameLoading); this is a maintained fork with compatibility fixes and performance improvements.

## Installation

> [!IMPORTANT]
> Requires RimWorld 1.6 and [Harmony](https://github.com/pardeike/HarmonyRimWorld/releases/latest). Load this mod after Harmony.

- **Steam Workshop**: subscribe on the [Workshop page](https://steamcommunity.com/sharedfiles/filedetails/?id=3652938473).
- **Manual**: download or clone this repository into your `RimWorld/Mods/` folder, then enable **Faster Game Loading - Continued** in the in-game mod list.

All options live in `Options → Mod options → Faster Game Loading - Continued`. Most players can keep the defaults; see [Recommended Settings](#recommended-settings).

## Startup Flow

```mermaid
graph TD
    A[Game startup] --> B[Assemblies and type reflection]
    B --> C[Mod content loading]
    C --> D[XML / Defs loading]
    D --> G[Main menu]
    G --> H[Texture loading]
    H --> I[Downscaled texture cache]
    H --> J[Static atlas baking]
    J --> K[Adaptive atlas baking]
    G --> L[Delay graphic and icon loading]
```

## Features

Enabled by default:

- **Load mod content early**: Processes pending mod content during idle loading gaps before RimWorld's normal `ReloadContentInt` pass reaches those mods. It starts only after every mod constructor has run (at `LoadModXML`), so FGL's and other mods' Harmony patches already apply to early-loaded content. It stops once `PlayDataLoader.Loaded` is true and does not restart on language changes. Texture byte preloading is skipped when Graphics Settings+ or Image Opt is active; bytes already read on the main thread are not prefetched again.
- **Multi-threaded preloading**: Loads XML assets in parallel while preserving RimWorld's original load-folder override order. With Hyperdrive, `Defs/` XML loading is left to Hyperdrive. A background thread also reads original texture files into memory in the order mods load them, holding at most 256 MB at a time, so the main thread does not wait on disk reads while loading textures. Files the main thread skips are released, and the rest is freed when startup finishes. This is skipped when Graphics Settings+ or Image Opt loads textures.
- **Type lookup cache**: Warms full type names before XML parsing and remembers resolved names across sessions. It follows RimWorld's assembly search order, refreshes when the mod list or any game/mod assembly changes, and falls back to RimWorld's lookup for stale entries. Harmony `AccessTools.TypeByName` keeps a separate per-session cache. Each assembly's types are listed once and shared with the `AccessTools.AllTypes` cache. Changes take effect after restarting the game.
- **Trim static atlases**: RimWorld packs each static atlas into a power-of-two height and doubles that height when the first attempt does not fit, so large atlases are often half empty rows. Each atlas keeps one empty row per mipmap level, with its height rounded up so every mip level is a multiple of 8 pixels for GPU compression. Every texture keeps its pixel position. Applies to RimWorld's baking and to **Adaptive atlas baking**. Changes take effect after restarting the game.
- **Skip duplicate atlas textures**: RimWorld queues a minifiable building's texture for the Building, Item and Misc atlases. Item and Misc copies are skipped when the texture is also queued for Building and its mask state is consistent across all queued groups. Copies with conflicting mask states are preserved so cross-group lookup cannot select the wrong mask. RimWorld's "found in another atlas group" warning is silenced for removed copies only. Changes take effect after restarting the game.

Disabled by default:

- **Delay graphic and icon loading**: Moves some non-essential visual and icon work to batched processing after entering the game. Essential categories such as furniture are chosen after Def references resolve. RimWorld's `ResolveIcon` handles icons before deferred atlas baking. Changes take effect after restarting the game.
- **Adaptive atlas baking**: Only takes effect together with **Delay graphic and icon loading**. The deferred static atlases are baked one atlas per frame, sized from the measured GPU speed but never smaller than 1024×1024 pixels, so rendering still benefits from batching. Without delayed loading, RimWorld's original baking is used. Known risky race/multi-mask textures are kept out of static atlases. Changes take effect after restarting the game.
- **Faster atlas compression**: On the CPU path, compresses the baked static atlases with `Texture2D.Compress(highQuality: false)` instead of `true`. The high-quality mode adds dithering and takes longer. GPU compression is unchanged. Changes take effect after restarting the game.
- **Verbose logging**: Prints debugging messages.

Manual tool:

- **Downscale textures**: Shows a loading screen while it downscales high-resolution textures into a separate cache (in successive halving steps to avoid aliasing). Original mod files are never modified, and cached textures use RimWorld's filtering and compression pipeline. Textures under `/UI/` keep mipmaps disabled. Original files are read and the cache PNGs are encoded and written on background threads; only decoding and GPU resizing run on the main thread. When the mod list changes, cache entries for still-active mods are retained. Rebuilding replaces the previous cache only after every new file is in place; if any cache file fails to write, or nothing could be written, the previous cache is kept. If you already use Graphics Settings+ or RimSort Optimize Texture, you usually do not need this. While Graphics Settings+ or Image Opt is loading textures, the tool is unavailable and the button is hidden.
- **Clear texture cache**: Shows a loading screen while it removes cached downscaled textures, so original textures are used on the next startup.

> [!NOTE]
> Brief startup unresponsiveness can be normal, especially with large mod lists. Startup sound playback is temporarily held until deferred sound definitions finish resolving, then released automatically.
> **Delay graphic and icon loading** is an advanced option. If you see texture or icon timing issues, disable it first.
> Downscaled texture cache can be cleared from the mod settings.

## Compatibility

Compatibility handling exists for:

- [Loading Progress](https://github.com/ilyvion/LoadingProgress)
- [Graphics Settings+](https://github.com/RealTelefonmast/GraphicsSetter)
- [HugsLib](https://github.com/UnlimitedHugs/RimworldHugsLib)
- [AyaTweaks](https://gitlab.com/WRelicK/AyaTweaks2.0) / Ayameduki mods
- [Humanoid Alien Races](https://github.com/erdelf/AlienRaces)
- [Ancot Library](https://steamcommunity.com/sharedfiles/filedetails/?id=2988801276)
- [ChezhouLib](https://steamcommunity.com/sharedfiles/filedetails/?id=3595247479)
- [Hyperdrive](https://github.com/vopaga/rimworld-hyperdrive)

Important behavior:

- With [Hyperdrive](https://github.com/vopaga/rimworld-hyperdrive), Faster Game Loading leaves parallel `Defs/` XML loading to Hyperdrive, which loads mods in parallel, and still parallelizes `Patches/` XML within each mod. Hyperdrive turns off its parallel mod loading when another mod has already patched `LoadModXML`. RimWorld does not construct mod classes in load order, so Faster Game Loading waits until Hyperdrive is constructed before it patches `LoadModXML`. This works in any load order. If Hyperdrive still turns off its parallel loading, Faster Game Loading keeps parsing `Defs/` in parallel itself.

- [Missile Girl - Performance Mod](https://github.com/ViralReaction/MissileGirl) and [DefLoadCache](https://github.com/FluxxField/rimworld-defload-cache) have no dedicated compatibility code in this mod. They are expected to work alongside Faster Game Loading, but use either Missile Girl or DefLoadCache, not both.
- [Image Opt](https://steamcommunity.com/sharedfiles/filedetails/?id=3543873568) compatibility is no longer maintained. When Faster Game Loading and Image Opt are enabled together, mods that depend on [Ancot Library](https://steamcommunity.com/sharedfiles/filedetails/?id=2988801276) may encounter graphical loading errors, missing textures, or black textures under some loading conditions. Using both mods together is not recommended.
- Existing Image Opt safeguards, such as downscaled-texture bypass and invalid `.dds` / `.dds.zstd` cache cleanup, do not guarantee compatibility.
- HAR and Ancot-related race mods skip some early-loading and atlas-baking paths to reduce bodyAddon, hair, ear, and multi-mask texture issues.

## Recommended Settings

Most players should start with the defaults.

If loading is still slow:

1. Keep **Load mod content early**, **Multi-threaded preloading**, and **Type lookup cache** enabled.
2. For large texture-heavy mod lists, prefer Graphics Settings+ or RimSort Optimize Texture. Image Opt compatibility is no longer maintained.
3. If you do not use an external texture tool, consider FGL's **Downscale textures** tool.
4. Enable **Delay graphic and icon loading** or **Adaptive atlas baking** only if you are willing to troubleshoot compatibility issues.

## Project Structure

```text
FasterGameLoading/
├── About/                         # Mod metadata, preview, and Workshop id
├── Assemblies/                    # Compiled DLL
├── LanguageData/                  # Translation XML files (EN, zh-TW, zh-CN, RU)
├── SteamDescriptions/             # Steam Workshop description sources
├── Source/
│   ├── Core/                      # Mod entry point and startup cleanup
│   ├── Settings/                  # Settings and cross-session cache data
│   ├── XMLLoadingCache/           # Parallel XML loading
│   ├── EarlyModContentLoading/    # Load mod content early and reflection cache
│   ├── TextureDownscaler/         # Downscale textures and cache loading
│   ├── AdaptiveAtlasBaking/       # Adaptive atlas baking
│   ├── StaticAtlasOptimizations/  # Trim, skip duplicates and compress static atlases
│   ├── DelayGraphicAndIconLoading/# Delay graphic and icon loading
│   ├── DelaySoundLoading/         # Deferred sound resolution
│   ├── Compatibility/             # Third-party mod compatibility handling
│   ├── Language/                  # In-game translation injection
│   ├── Utilities/                 # Shared helpers
│   └── FasterGameLoading.Tests/   # NUnit tests
├── LICENSE
└── README.md
```

## Building from Source

The main project targets .NET Framework 4.7.2 and references RimWorld via the [Krafs.Rimworld.Ref](https://www.nuget.org/packages/Krafs.Rimworld.Ref) NuGet package, so no local RimWorld installation is needed to compile. The compiled DLL is written to `Assemblies/`.

```bash
dotnet build Source/FasterGameLoading.csproj -c Release
```

Tests use NUnit on .NET 9:

```bash
dotnet test Source/FasterGameLoading.Tests/FasterGameLoading.Tests.csproj --settings test.runsettings
```

## Credits and License

Original mod by [Taranchuk](https://github.com/Taranchuk/FasterGameLoading). This fork is maintained by [mushroomTW](https://github.com/mushroomTW/FasterGameLoading---Continued) with compatibility fixes and performance improvements.
