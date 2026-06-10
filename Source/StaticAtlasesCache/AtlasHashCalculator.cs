using System.Linq;
using System.Security.Cryptography;
using System.Text;
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
        /// </summary>
        public static string ComputeModsHash()
        {
            var activeMods = ModsConfig.ActiveModsInLoadOrder.Select(x => x.packageIdLowerCase).ToList();
            var str = string.Join(",", activeMods);
            using var md5 = MD5.Create();
            return string.Concat(md5.ComputeHash(Encoding.UTF8.GetBytes(str)).Select(b => b.ToString("x2")));
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

            return (texture?.name ?? "<null>") + "#" + texture?.GetInstanceID();
        }
    }
}
