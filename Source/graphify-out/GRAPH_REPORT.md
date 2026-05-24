# Graph Report - Source  (2026-05-24)

## Corpus Check
- 45 files · ~11,052 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 544 nodes · 551 edges · 51 communities (37 shown, 14 thin omitted)
- Extraction: 100% EXTRACTED · 0% INFERRED · 0% AMBIGUOUS
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `c57b9d08`
- Run `git rev-parse HEAD` and compare to check if the graph is stale.
- Run `graphify update .` after code changes (no API cost).

## Community Hubs (Navigation)
- [[_COMMUNITY_Community 0|Community 0]]
- [[_COMMUNITY_Community 1|Community 1]]
- [[_COMMUNITY_Community 2|Community 2]]
- [[_COMMUNITY_Community 3|Community 3]]
- [[_COMMUNITY_Community 4|Community 4]]
- [[_COMMUNITY_Community 5|Community 5]]
- [[_COMMUNITY_Community 6|Community 6]]
- [[_COMMUNITY_Community 7|Community 7]]
- [[_COMMUNITY_Community 8|Community 8]]
- [[_COMMUNITY_Community 9|Community 9]]
- [[_COMMUNITY_Community 10|Community 10]]
- [[_COMMUNITY_Community 11|Community 11]]
- [[_COMMUNITY_Community 12|Community 12]]
- [[_COMMUNITY_Community 13|Community 13]]
- [[_COMMUNITY_Community 14|Community 14]]
- [[_COMMUNITY_Community 15|Community 15]]
- [[_COMMUNITY_Community 16|Community 16]]
- [[_COMMUNITY_Community 17|Community 17]]
- [[_COMMUNITY_Community 18|Community 18]]
- [[_COMMUNITY_Community 19|Community 19]]
- [[_COMMUNITY_Community 20|Community 20]]
- [[_COMMUNITY_Community 21|Community 21]]
- [[_COMMUNITY_Community 22|Community 22]]
- [[_COMMUNITY_Community 23|Community 23]]
- [[_COMMUNITY_Community 24|Community 24]]
- [[_COMMUNITY_Community 25|Community 25]]
- [[_COMMUNITY_Community 26|Community 26]]
- [[_COMMUNITY_Community 27|Community 27]]
- [[_COMMUNITY_Community 28|Community 28]]
- [[_COMMUNITY_Community 29|Community 29]]
- [[_COMMUNITY_Community 30|Community 30]]
- [[_COMMUNITY_Community 31|Community 31]]
- [[_COMMUNITY_Community 32|Community 32]]
- [[_COMMUNITY_Community 33|Community 33]]
- [[_COMMUNITY_Community 34|Community 34]]
- [[_COMMUNITY_Community 35|Community 35]]
- [[_COMMUNITY_Community 36|Community 36]]
- [[_COMMUNITY_Community 37|Community 37]]
- [[_COMMUNITY_Community 38|Community 38]]
- [[_COMMUNITY_Community 39|Community 39]]
- [[_COMMUNITY_Community 40|Community 40]]
- [[_COMMUNITY_Community 41|Community 41]]
- [[_COMMUNITY_Community 42|Community 42]]
- [[_COMMUNITY_Community 43|Community 43]]
- [[_COMMUNITY_Community 44|Community 44]]
- [[_COMMUNITY_Community 45|Community 45]]
- [[_COMMUNITY_Community 46|Community 46]]

## God Nodes (most connected - your core abstractions)
1. `compile` - 109 edges
2. `DelayedActions` - 17 edges
3. `TextureCacheManager` - 16 edges
4. `TextureScanner` - 16 edges
5. `restore` - 14 edges
6. `restore` - 14 edges
7. `TextureResize` - 12 edges
8. `StaticAtlasCache` - 9 edges
9. `SoundStarter_Patch` - 8 edges
10. `FasterGameLoadingMod` - 7 edges

## Surprising Connections (you probably didn't know these)
- None detected - all connections are within the same source files.

## Communities (51 total, 14 thin omitted)

### Community 0 - "Community 0"
Cohesion: 0.02
Nodes (108): ref/net472/Assembly-CSharp.dll, ref/net472/Assembly-CSharp-firstpass.dll, ref/net472/Assembly-CSharp_publicised.dll, ref/net472/com.rlabrecque.steamworks.net.dll, ref/net472/ISharpZipLib.dll, ref/net472/Mono.Security.dll, ref/net472/mscorlib.dll, ref/net472/NAudio.dll (+100 more)

### Community 1 - "Community 1"
Cohesion: 0.05
Nodes (41): Krafs.Publicizer, Krafs.Rimworld.Ref, Lib.Harmony, net472, include, suppressParent, target, version (+33 more)

### Community 2 - "Community 2"
Cohesion: 0.06
Nodes (32): frameworks, restore, version, format, net472, framework, projectReferences, runtimeIdentifierGraphPath (+24 more)

### Community 3 - "Community 3"
Cohesion: 0.15
Nodes (7): bool, int, DelayedActions, FasterGameLoading, MonoBehaviour, Queue, Stopwatch

### Community 4 - "Community 4"
Cohesion: 0.12
Nodes (6): ConcurrentDictionary, Dictionary, object, string, FasterGameLoading, TextureCacheManager

### Community 5 - "Community 5"
Cohesion: 0.22
Nodes (3): Dictionary, FasterGameLoading, TextureScanner

### Community 6 - "Community 6"
Cohesion: 0.19
Nodes (8): AtlasInfo, bool, int, List, string, FasterGameLoading, Manifest, StaticAtlasCache

### Community 7 - "Community 7"
Cohesion: 0.05
Nodes (40): build/Krafs.Publicizer.props, build/Krafs.Publicizer.targets, lib/net472/0Harmony.dll, contentfiles/cs/any/Publicizer/IgnoresAccessChecksToAttribute.cs, buildAction, codeLanguage, copyToOutput, build (+32 more)

### Community 8 - "Community 8"
Cohesion: 0.21
Nodes (3): long, FasterGameLoading, TextureResize

### Community 9 - "Community 9"
Cohesion: 0.15
Nodes (13): Krafs.Publicizer, Krafs.Rimworld.Ref, Lib.Harmony, include, suppressParent, target, version, target (+5 more)

### Community 10 - "Community 10"
Cohesion: 0.29
Nodes (3): string, FasterGameLoading, FGLLog

### Community 11 - "Community 11"
Cohesion: 0.50
Nodes (3): string, FasterGameLoading, FGLConsts

### Community 12 - "Community 12"
Cohesion: 0.20
Nodes (3): bool, FasterGameLoading, SoundStarter_Patch

### Community 13 - "Community 13"
Cohesion: 0.22
Nodes (6): FasterGameLoading, FasterGameLoadingMod, DelayedActions, FasterGameLoadingSettings, Harmony, Mod

### Community 14 - "Community 14"
Cohesion: 0.22
Nodes (6): float, Dictionary, int, List, FasterGameLoading, SessionCache

### Community 15 - "Community 15"
Cohesion: 0.29
Nodes (3): FasterGameLoading, RedirectHugslibToMainThread, MethodBase

### Community 16 - "Community 16"
Cohesion: 0.29
Nodes (3): FasterGameLoading, FGLProgressReporter, FieldInfo

### Community 18 - "Community 18"
Cohesion: 0.33
Nodes (3): ConcurrentDictionary, FasterGameLoading, GraphicData_Init_Patch

### Community 19 - "Community 19"
Cohesion: 0.29
Nodes (4): AccessTools_AllTypes_Patch, List, object, FasterGameLoading

### Community 20 - "Community 20"
Cohesion: 0.29
Nodes (3): Dictionary, FasterGameLoading, GenTypes_GetTypeInAnyAssemblyInt_Patch

### Community 21 - "Community 21"
Cohesion: 0.29
Nodes (3): string, FasterGameLoading, FGLLog

### Community 22 - "Community 22"
Cohesion: 0.29
Nodes (4): ModSettings, bool, FasterGameLoading, FasterGameLoadingSettings

### Community 23 - "Community 23"
Cohesion: 0.29
Nodes (4): ConcurrentDictionary, int, FasterGameLoading, ModContentLoaderTexture2D_LoadTexture_Patch

### Community 24 - "Community 24"
Cohesion: 0.33
Nodes (3): Dictionary, FasterGameLoading, TextureResizer

### Community 26 - "Community 26"
Cohesion: 0.40
Nodes (3): AlienRacesCompat, bool, FasterGameLoading

### Community 27 - "Community 27"
Cohesion: 0.33
Nodes (3): Dictionary, FasterGameLoading, GenTypes_AllLeafSubclasses_Patch

### Community 28 - "Community 28"
Cohesion: 0.33
Nodes (3): HashSet, FasterGameLoading, ModAssetBundlesHandler_ReloadAll_Patch

### Community 29 - "Community 29"
Cohesion: 0.33
Nodes (3): HashSet, FasterGameLoading, ModContentPack_ReloadContentInt_Patch

### Community 31 - "Community 31"
Cohesion: 0.33
Nodes (3): CacheResetter, List, FasterGameLoading

### Community 32 - "Community 32"
Cohesion: 0.33
Nodes (3): ConcurrentDictionary, FasterGameLoading, XmlNode_SelectSingleNode_Patch

### Community 38 - "Community 38"
Cohesion: 0.40
Nodes (4): DocumentGroupContainers, Documents, Version, WorkspaceRootPath

### Community 39 - "Community 39"
Cohesion: 0.40
Nodes (4): DocumentGroupContainers, Documents, Version, WorkspaceRootPath

### Community 42 - "Community 42"
Cohesion: 0.50
Nodes (3): string, FasterGameLoading, FGLConsts

## Knowledge Gaps
- **273 isolated node(s):** `Version`, `WorkspaceRootPath`, `Documents`, `DocumentGroupContainers`, `Version` (+268 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **14 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `compile` connect `Community 0` to `Community 46`, `Community 7`?**
  _High betweenness centrality (0.101) - this node is a cross-community bridge._
- **Why does `Krafs.Rimworld.Ref/1.6.4633` connect `Community 7` to `Community 0`?**
  _High betweenness centrality (0.061) - this node is a cross-community bridge._
- **What connects `Version`, `WorkspaceRootPath`, `Documents` to the rest of the system?**
  _273 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Community 0` be split into smaller, more focused modules?**
  _Cohesion score 0.018518518518518517 - nodes in this community are weakly interconnected._
- **Should `Community 1` be split into smaller, more focused modules?**
  _Cohesion score 0.05 - nodes in this community are weakly interconnected._
- **Should `Community 2` be split into smaller, more focused modules?**
  _Cohesion score 0.0625 - nodes in this community are weakly interconnected._
- **Should `Community 3` be split into smaller, more focused modules?**
  _Cohesion score 0.14619883040935672 - nodes in this community are weakly interconnected._