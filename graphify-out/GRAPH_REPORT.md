# Graph Report - FasterGameLoading---Continued  (2026-05-25)

## Corpus Check
- 46 files · ~25,383 words
- Verdict: corpus is large enough that graph structure adds value.

## Summary
- 646 nodes · 755 edges · 54 communities (40 shown, 14 thin omitted)
- Extraction: 99% EXTRACTED · 1% INFERRED · 0% AMBIGUOUS · INFERRED: 5 edges (avg confidence: 0.86)
- Token cost: 0 input · 0 output

## Graph Freshness
- Built from commit: `c59a2916`
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
- [[_COMMUNITY_Community 46|Community 46]]
- [[_COMMUNITY_Community 47|Community 47]]
- [[_COMMUNITY_Community 49|Community 49]]
- [[_COMMUNITY_Community 50|Community 50]]
- [[_COMMUNITY_Community 51|Community 51]]
- [[_COMMUNITY_Community 52|Community 52]]
- [[_COMMUNITY_Community 53|Community 53]]

## God Nodes (most connected - your core abstractions)
1. `compile` - 109 edges
2. `TextureResize` - 50 edges
3. `DelayedActions` - 21 edges
4. `TextureCacheManager` - 16 edges
5. `TextureScanner` - 16 edges
6. `restore` - 14 edges
7. `restore` - 14 edges
8. `SessionCache` - 11 edges
9. `FasterGameLoading Mod` - 11 edges
10. `StaticAtlasCache` - 10 edges

## Surprising Connections (you probably didn't know these)
- `FasterGameLoading Mod` --has_icon--> `Mod Icon Image`  [INFERRED]
  README.md → About/ModIcon.png
- `FasterGameLoading Mod` --has_preview--> `Mod Preview Image`  [INFERRED]
  README.md → About/Preview.png
- `FasterGameLoading Mod` --references--> `Debug Build Artifacts`  [INFERRED]
  README.md → Source/obj/Debug/FasterGameLoading.csproj.FileListAbsolute.txt
- `FasterGameLoading Mod` --references--> `Release Build Artifacts`  [INFERRED]
  README.md → Source/obj/Release/FasterGameLoading.csproj.FileListAbsolute.txt
- `DelayedActions` --references--> `bool`  [EXTRACTED]
  Source/Delay graphic and icon loading/DelayedActions.cs → Delay graphic and icon loading/DelayedActions.cs

## Communities (54 total, 14 thin omitted)

### Community 0 - "Community 0"
Cohesion: 0.02
Nodes (108): ref/net472/Assembly-CSharp.dll, ref/net472/Assembly-CSharp-firstpass.dll, ref/net472/Assembly-CSharp_publicised.dll, ref/net472/com.rlabrecque.steamworks.net.dll, ref/net472/ISharpZipLib.dll, ref/net472/Mono.Security.dll, ref/net472/mscorlib.dll, ref/net472/NAudio.dll (+100 more)

### Community 1 - "Community 1"
Cohesion: 0.05
Nodes (43): build/Krafs.Publicizer.props, build/Krafs.Publicizer.targets, lib/net472/0Harmony.dll, contentfiles/cs/any/Publicizer/IgnoresAccessChecksToAttribute.cs, buildAction, codeLanguage, copyToOutput, build (+35 more)

### Community 2 - "Community 2"
Cohesion: 0.08
Nodes (11): long, ConcurrentDictionary, Dictionary, object, string, ConcurrentDictionary, Dictionary, object (+3 more)

### Community 3 - "Community 3"
Cohesion: 0.06
Nodes (32): frameworks, restore, version, format, net472, framework, projectReferences, runtimeIdentifierGraphPath (+24 more)

### Community 4 - "Community 4"
Cohesion: 0.05
Nodes (38): Krafs.Publicizer, Krafs.Rimworld.Ref, Lib.Harmony, net472, include, suppressParent, target, version (+30 more)

### Community 5 - "Community 5"
Cohesion: 0.16
Nodes (12): bool, int, List, string, AtlasInfo, bool, int, List (+4 more)

### Community 6 - "Community 6"
Cohesion: 0.13
Nodes (9): bool, int, DelayedActions, FasterGameLoading, MonoBehaviour, Queue, bool, int (+1 more)

### Community 7 - "Community 7"
Cohesion: 0.29
Nodes (3): string, FasterGameLoading, FGLLog

### Community 8 - "Community 8"
Cohesion: 0.12
Nodes (6): ConcurrentDictionary, Dictionary, object, string, FasterGameLoading, TextureCacheManager

### Community 9 - "Community 9"
Cohesion: 0.18
Nodes (11): Compatible Mods (Loading Progress, DefLoadCache, Image Opt), Debug Build Artifacts, Delayed Non-Essential Texture Loading, Early Mod Content Loading, FasterGameLoading Mod, Mod Icon Image, Mod Preview Image, Steam Workshop ID 3652938473 (+3 more)

### Community 10 - "Community 10"
Cohesion: 0.22
Nodes (6): FasterGameLoading, FasterGameLoadingMod, DelayedActions, FasterGameLoadingSettings, Harmony, Mod

### Community 11 - "Community 11"
Cohesion: 0.17
Nodes (9): float, Dictionary, int, List, FasterGameLoading, SessionCache, Dictionary, int (+1 more)

### Community 12 - "Community 12"
Cohesion: 0.28
Nodes (3): FasterGameLoading, RedirectHugslibToMainThread, MethodBase

### Community 13 - "Community 13"
Cohesion: 0.20
Nodes (3): FasterGameLoading, SoundStarter_Patch, bool

### Community 15 - "Community 15"
Cohesion: 0.27
Nodes (4): ConcurrentDictionary, FasterGameLoading, GraphicData_Init_Patch, ConcurrentDictionary

### Community 16 - "Community 16"
Cohesion: 0.22
Nodes (6): AccessTools_AllTypes_Patch, List, object, FasterGameLoading, List, object

### Community 17 - "Community 17"
Cohesion: 0.22
Nodes (5): Dictionary, FasterGameLoading, GenTypes_GetTypeInAnyAssemblyInt_Patch, ConcurrentDictionary, Dictionary

### Community 18 - "Community 18"
Cohesion: 0.25
Nodes (5): ModSettings, bool, FasterGameLoading, FasterGameLoadingSettings, bool

### Community 19 - "Community 19"
Cohesion: 0.22
Nodes (6): ConcurrentDictionary, int, ConcurrentDictionary, int, FasterGameLoading, ModContentLoaderTexture2D_LoadTexture_Patch

### Community 21 - "Community 21"
Cohesion: 0.32
Nodes (4): AlienRacesCompat, bool, FasterGameLoading, bool

### Community 22 - "Community 22"
Cohesion: 0.29
Nodes (4): Dictionary, FasterGameLoading, GenTypes_AllLeafSubclasses_Patch, Dictionary

### Community 23 - "Community 23"
Cohesion: 0.29
Nodes (4): HashSet, FasterGameLoading, ModAssetBundlesHandler_ReloadAll_Patch, HashSet

### Community 24 - "Community 24"
Cohesion: 0.29
Nodes (4): HashSet, FasterGameLoading, ModContentPack_ReloadContentInt_Patch, HashSet

### Community 25 - "Community 25"
Cohesion: 0.29
Nodes (4): List, CacheResetter, List, FasterGameLoading

### Community 26 - "Community 26"
Cohesion: 0.29
Nodes (4): ConcurrentDictionary, ConcurrentDictionary, FasterGameLoading, XmlNode_SelectSingleNode_Patch

### Community 32 - "Community 32"
Cohesion: 0.53
Nodes (4): DocumentGroupContainers, Documents, Version, WorkspaceRootPath

### Community 33 - "Community 33"
Cohesion: 0.53
Nodes (4): DocumentGroupContainers, Documents, Version, WorkspaceRootPath

### Community 46 - "Community 46"
Cohesion: 0.29
Nodes (3): FasterGameLoading, FGLProgressReporter, FieldInfo

### Community 49 - "Community 49"
Cohesion: 0.22
Nodes (3): Dictionary, FasterGameLoading, TextureScanner

### Community 50 - "Community 50"
Cohesion: 0.33
Nodes (3): Dictionary, FasterGameLoading, TextureResizer

### Community 52 - "Community 52"
Cohesion: 0.50
Nodes (3): string, FasterGameLoading, FGLConsts

### Community 53 - "Community 53"
Cohesion: 0.15
Nodes (13): Krafs.Publicizer, Krafs.Rimworld.Ref, Lib.Harmony, include, suppressParent, target, version, target (+5 more)

## Knowledge Gaps
- **266 isolated node(s):** `FasterGameLoading`, `FieldInfo`, `MethodBase`, `Harmony`, `FasterGameLoadingSettings` (+261 more)
  These have ≤1 connection - possible missing edges or undocumented components.
- **14 thin communities (<3 nodes) omitted from report** — run `graphify query` to explore isolated nodes.

## Suggested Questions
_Questions this graph is uniquely positioned to answer:_

- **Why does `compile` connect `Community 0` to `Community 1`, `Community 38`?**
  _High betweenness centrality (0.072) - this node is a cross-community bridge._
- **Why does `Krafs.Rimworld.Ref/1.6.4633` connect `Community 1` to `Community 0`?**
  _High betweenness centrality (0.044) - this node is a cross-community bridge._
- **What connects `FasterGameLoading`, `FieldInfo`, `MethodBase` to the rest of the system?**
  _266 weakly-connected nodes found - possible documentation gaps or missing edges._
- **Should `Community 0` be split into smaller, more focused modules?**
  _Cohesion score 0.018518518518518517 - nodes in this community are weakly interconnected._
- **Should `Community 1` be split into smaller, more focused modules?**
  _Cohesion score 0.052525252525252523 - nodes in this community are weakly interconnected._
- **Should `Community 2` be split into smaller, more focused modules?**
  _Cohesion score 0.08295625942684766 - nodes in this community are weakly interconnected._
- **Should `Community 3` be split into smaller, more focused modules?**
  _Cohesion score 0.06417112299465241 - nodes in this community are weakly interconnected._