using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 負責計算 Mod 組合雜湊與烘焙佇列（buildQueue）的雜湊，
    /// 藉此作為快取失效判定依據。
    /// </summary>
    public static class AtlasHashCalculator
    {
        /// <summary>
        /// 計算目前載入 mod 組合的 MD5 hash，用於驗證快取是否過期。
        /// 除了 mod 清單之外，還折入以下影響烘焙輸出的因子：
        /// (a) BakingSkipList 已解析的排除根目錄（若已初始化），確保跨啟動非確定性
        ///     的 IsAlienRaceMod 偵測結果改變時能使舊快取失效；
        /// (b) Unity / 遊戲版本字串，避免引擎升級後舊格式快取被誤命中；
        /// (c) 會影響烘焙輸出的模組設定（目前為壓縮開關）。
        /// </summary>
        public static string ComputeModsHash()
        {
            var activeMods = ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase).ToList();
            var sb = new StringBuilder();
            sb.Append(string.Join(",", activeMods));

            // (a) BakingSkipList 排除根目錄（已排序，跨啟動穩定）
            //     若尚未初始化（RunningMods 還沒就緒）則略過，
            //     此時 queueHash 的紋理鍵會涵蓋差異，可接受。
            var skipRoots = SmartBakingSkipList.GetResolvedSkipRootsForHash();
            if (skipRoots != null)
            {
                sb.Append("|skip:");
                foreach (var root in skipRoots)
                {
                    sb.Append(root).Append(';');
                }
            }

            // (b) Unity 版本與遊戲版本，引擎升級後格式可能改變
            sb.Append("|unity:").Append(Application.unityVersion);
            sb.Append("|game:").Append(RimWorld.VersionControl.CurrentVersionString);

            // (c) 影響烘焙輸出的設定：壓縮開關決定是否走 DXT5 壓縮路徑
            sb.Append("|prefs.tc:").Append(Prefs.TextureCompression ? 1 : 0);

            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Select(b => b.ToString("x2")));
        }

        /// <summary>
        /// 計算 buildQueue 的 MD5 hash，用於驗證快取是否過期。
        /// </summary>
        public static string ComputeQueueHash()
        {
            var sb = new StringBuilder();
            var orderedKeys = GlobalTextureAtlasManager.buildQueue.Keys.OrderBy(k => (int)k.group).ThenBy(k => k.hasMask).ToList();
            foreach (var key in orderedKeys)
            {
                sb.Append((int)key.group).Append('|').Append(key.hasMask).Append('|');
                var texList = GlobalTextureAtlasManager.buildQueue[key].Item1
                    .OrderBy(GetTextureKey)
                    .ToList();
                foreach (var tex in texList)
                {
                    sb.Append(GetTextureKey(tex)).Append('|').Append(tex.width).Append('|').Append(tex.height).Append('|');
                    if (key.hasMask && GlobalTextureAtlasManager.buildQueueMasks.TryGetValue(tex, out var mask))
                    {
                        sb.Append(GetTextureKey(mask)).Append('|').Append(mask.width).Append('|').Append(mask.height).Append('|');
                    }
                }
            }
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString())).Select(b => b.ToString("x2")));
        }

        public static string GetTextureKey(UnityEngine.Texture texture)
        {
            if (ModContentLoaderTexture2D_LoadTexture_Patch.TryGetSavedTexturePath(texture, out var path))
            {
                return path.Replace('\\', '/');
            }

            // GetInstanceID() 每次啟動都不同，改用名稱+尺寸組成穩定備用鍵，避免持久化雜湊每次失效
            return (texture?.name ?? "<null>") + "#" + texture?.width + "x" + texture?.height;
        }
    }
}
