using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace FasterGameLoading
{
    /// <summary>
    /// 本 session 已載入的 Mod 貼圖登記：路徑 ↔ 貼圖雙向查詢，以及排除靜態圖集烘焙的貼圖實體。
    /// 由 LoadTexture 補丁寫入；降質工具的掃描與烘焙排除名單只透過這裡讀取。
    /// 路徑 ↔ 貼圖以弱參照持有，不會留住 Unity 貼圖；排除烘焙的名單則是強參照，直到語言切換才清空。
    /// </summary>
    public static class LoadedTextureRegistry
    {
        /// <summary>完整檔案路徑 → 貼圖。</summary>
        private static readonly ConcurrentDictionary<string, WeakReference<Texture2D>> texturesByPath =
            new ConcurrentDictionary<string, WeakReference<Texture2D>>(StringComparer.Ordinal);

        /// <summary>
        /// O(1) 反向查找表：貼圖 → 路徑。ConditionalWeakTable 以弱鍵追蹤，貼圖被 GC 時自動移除條目。
        /// 值必須為 reference type，故以可變 holder 包裹路徑，同一貼圖換路徑時就地覆寫。
        /// </summary>
        private static readonly ConditionalWeakTable<Texture2D, StringHolder> pathsByTexture = new ConditionalWeakTable<Texture2D, StringHolder>();

        /// <summary>
        /// 排除靜態圖集烘焙的貼圖實體。以強參照為鍵：只收受保護 Mod 的貼圖，數量有限，
        /// 但被替換或銷毀的貼圖仍會留在這裡，直到語言切換時 <see cref="Clear"/>。
        /// </summary>
        private static readonly ConcurrentDictionary<Texture2D, bool> skippedForBaking = new ConcurrentDictionary<Texture2D, bool>();

        private sealed class StringHolder
        {
            public string Value;
            public StringHolder(string value) { Value = value; }
        }

        static LoadedTextureRegistry()
        {
            SessionLifecycle.On(LifecyclePhase.LanguageReloading, Clear);
        }

        /// <summary>清空所有登記（語言切換時舊貼圖會被銷毀重載）。</summary>
        internal static void Clear()
        {
            texturesByPath.Clear();
            // ConditionalWeakTable 沒有 Clear API，且其條目隨鍵被 GC 自動消失；
            // 語言切換時舊貼圖通常仍活著，殘留條目只會在後續被新條目覆寫或隨 GC 移除，不影響正確性。
            skippedForBaking.Clear();
        }

        /// <summary>登記 <paramref name="fullPath"/> 目前對應的貼圖；同一路徑的舊貼圖會從反向查找表移除。</summary>
        public static void Record(string fullPath, Texture2D texture)
        {
            if (ReferenceEquals(texture, null)) return;

            if (texturesByPath.TryGetValue(fullPath, out var oldRef) && oldRef.TryGetTarget(out var oldTexture))
            {
                pathsByTexture.Remove(oldTexture);
            }

            texturesByPath[fullPath] = new WeakReference<Texture2D>(texture);
            // GetValue 在鍵已存在時回傳舊 holder、不呼叫 factory；同一貼圖以新路徑重新登記時必須手動覆寫，
            // 否則反向查詢仍回傳舊路徑，導致掃描／縮圖歸因錯誤來源。
            if (pathsByTexture.TryGetValue(texture, out var holder))
            {
                holder.Value = fullPath;
            }
            else
            {
                pathsByTexture.GetValue(texture, _ => new StringHolder(fullPath));
            }
        }

        /// <summary>
        /// 取得 <paramref name="fullPath"/> 已登記且仍可用的貼圖。
        /// WeakReference 只追蹤 C# 物件：Unity 端已銷毀的貼圖仍取得回來，這裡以 Unity 的 null 比較排除。
        /// </summary>
        public static bool TryGetTexture(string fullPath, out Texture2D texture)
        {
            if (texturesByPath.TryGetValue(fullPath, out var weakRef) && weakRef.TryGetTarget(out texture) && texture != null)
            {
                return true;
            }
            texture = null;
            return false;
        }

        /// <summary>貼圖的來源檔案路徑；非 Texture2D（如 RenderTexture）或未登記的貼圖回傳 false。</summary>
        public static bool TryGetPath(Texture texture, out string fullPath)
        {
            if (texture is Texture2D t2d && !ReferenceEquals(t2d, null) && pathsByTexture.TryGetValue(t2d, out var holder))
            {
                fullPath = holder.Value;
                return true;
            }
            fullPath = null;
            return false;
        }

        /// <summary>所有 C# 物件仍存活的已登記貼圖與其路徑的快照。</summary>
        public static IReadOnlyList<KeyValuePair<Texture2D, string>> Snapshot()
        {
            var alive = new List<KeyValuePair<Texture2D, string>>();
            foreach (var kvp in texturesByPath)
            {
                if (kvp.Value.TryGetTarget(out var texture))
                {
                    alive.Add(new KeyValuePair<Texture2D, string>(texture, kvp.Key));
                }
            }
            return alive;
        }

        /// <summary>取消 <paramref name="fullPath"/> 的登記，下次載入同一路徑會重新讀檔。</summary>
        public static void Forget(string fullPath)
        {
            texturesByPath.TryRemove(fullPath, out _);
        }

        /// <summary>貼圖屬於排除烘焙的目標 Mod 時，登記它不進入靜態圖集。</summary>
        public static void MarkSkipBakingIfProtected(string fullPath, Texture2D texture)
        {
            if (texture != null && ProtectedMods.ShouldSkipBaking(fullPath))
            {
                skippedForBaking[texture] = true;
            }
        }

        /// <summary>貼圖是否已登記為排除靜態圖集烘焙。</summary>
        public static bool IsSkippedForBaking(Texture2D texture)
        {
            // ConcurrentDictionary.ContainsKey(null) 會拋例外，故 null 先短路。
            return texture != null && skippedForBaking.ContainsKey(texture);
        }
    }
}
