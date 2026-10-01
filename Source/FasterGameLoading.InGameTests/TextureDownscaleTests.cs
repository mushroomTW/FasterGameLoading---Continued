using System;
using System.Collections.Generic;
using System.IO;
using RimTestRedux;
using RimWorld.IO;
using UnityEngine;
using Verse;

namespace FasterGameLoading.InGameTests
{
    /// <summary>
    /// 降質快取在真實 Unity 上的載入結果：LoadImage、mipmap、壓縮都只能在遊戲內驗證。
    /// 不依賴事先跑過降質工具：測試自己產生「原始貼圖」與「降質快取」兩張 PNG，登記到 FGL 的快取對照表，
    /// 再透過原版 ModContentLoader&lt;Texture2D&gt;.LoadTexture（FGL 的 patch 所在）載入。
    /// 縮放本身另由 <see cref="DownscaledTextureKeepsSourceAspectRatio"/> 經降質工具的 ResizeTexture 產生快取。
    /// The resize itself is covered by <see cref="DownscaledTextureKeepsSourceAspectRatio"/>, which makes its
    /// cache through the downscaler's ResizeTexture.
    /// </summary>
    [TestSuite]
    internal static class TextureDownscaleTests
    {
        private const int OriginalSize = 64;
        private const int CachedSize = 32;

        /// <summary>
        /// 每邊與精確比例的容許差距：換算時四捨五入的 0.5，加上就近對齊到 4 的半個區塊 2。
        /// How far each side may be from the exact proportion: 0.5 from rounding, plus 2 (half a block)
        /// from aligning to the nearest multiple of 4.
        /// </summary>
        private const double MaxSideError = 2.5;

        /// <summary>
        /// 寬、高、目標尺寸。精確的短邊落在 4 像素區塊的不同位置。
        /// Width, height, target size. The exact short sides land at different places within a 4-pixel block.
        /// </summary>
        private static readonly (int Width, int Height, int Target)[] AspectRatioCases =
        {
            // AlignToBlockSize 註解的例子：精確 5，就近取整仍是 4
            // The example in AlignToBlockSize's comment: exact 5, and nearest rounding still gives 4
            (1024, 40, 128),
            // 精確 7：就近取整為 8，一律向下取整會壓成 4
            // Exact 7: nearest rounding gives 8, always rounding down would squash it to 4
            (1024, 56, 128),
            // 同上，直向
            // The same, portrait
            (56, 1024, 128),
            // 精確 153.6：取整 154、對齊 156，差 2.4，只比 MaxSideError 小 0.1；改動對齊方式時這組最先失敗
            // Exact 153.6: rounds to 154 and aligns to 156, 2.4 off, only 0.1 under MaxSideError;
            // this case fails first if the alignment changes
            (1000, 600, 256),
            // 短邊不足 4 像素，保留原值
            // Short side under 4 pixels, kept as it is
            (2048, 6, 512),
        };

        private static bool ExternalTextureToolActive => !TextureOwnership.FglOwnsTextureLoading;

        /// <summary>
        /// 降質快取載入的貼圖要與原版載入同一張 PNG 的結果一致（格式、濾波、mipmap；壓縮後的濾波設定也以原版為準）。
        /// 唯一刻意的差異：/UI/ 底下的貼圖不產生 mipmap，縮小顯示時才不會糊。
        /// </summary>
        [Test]
        public static void CachedTextureIsServedLikeVanilla()
        {
            foreach (var isUi in new[] { true, false })
            {
                using var probe = new Probe(isUi);
                int hitsBefore = ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits;

                var texture = probe.Load();

                if (ExternalTextureToolActive)
                {
                    // Graphics Settings+／Image Opt 接手貼圖載入時，FGL 必須完全讓開。
                    Assert.That(texture.width).Is.EqualTo(OriginalSize);
                    Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits).Is.EqualTo(hitsBefore);
                    continue;
                }
                Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits).Is.EqualTo(hitsBefore + 1);
                Assert.That(texture.name).Is.EqualTo(Probe.FileNameWithoutExtension);

                var vanilla = probe.LoadCacheFileDirectly();
                Assert.That(texture.width).Is.EqualTo(vanilla.width);
                Assert.That(texture.height).Is.EqualTo(vanilla.height);
                Assert.That(texture.format.ToString()).Is.EqualTo(vanilla.format.ToString());
                Assert.That(texture.filterMode.ToString()).Is.EqualTo(vanilla.filterMode.ToString());
                Assert.That(texture.anisoLevel).Is.EqualTo(vanilla.anisoLevel);
                Assert.That(texture.mipmapCount).Is.EqualTo(isUi ? 1 : vanilla.mipmapCount);

                // 同一路徑再載入一次要沿用同一個實體，不能重新讀檔。
                Assert.That(ReferenceEquals(probe.Load(), texture)).Is.True();
            }
        }

        /// <summary>原始檔被 mod 更新（大小或時間變了）後，舊的降質快取必須作廢、改載原始貼圖。</summary>
        [Test]
        public static void StaleCacheFallsBackToOriginalTexture()
        {
            if (ExternalTextureToolActive) return;

            using var probe = new Probe(isUi: false);
            File.SetLastWriteTimeUtc(probe.OriginalPath, DateTime.UtcNow.AddHours(1));
            int failuresBefore = ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures;

            var texture = probe.Load();

            Assert.That(texture.width).Is.EqualTo(OriginalSize);
            Assert.That(FasterGameLoadingMod.Instance.CacheManager.ResizedTextureCache.ContainsKey(probe.OriginalPath)).Is.False();
            // 過期屬正常失效，不是載入失敗。
            Assert.That(ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadFailures).Is.EqualTo(failuresBefore);
        }

        /// <summary>
        /// 降質工具本身的縮放：TextureResize.ResizeTexture 換算尺寸（等比例、對齊 4 的倍數）、以 RenderTexture 縮放並寫出 PNG，
        /// 再經 FGL 的快取載入。上面兩個測試自行產生快取 PNG，不經過這一步；長寬比的兩次修正（bc765bc、746918a）都在這裡。
        /// 長邊必須等於目標尺寸，兩邊與精確比例的差距都不得超過 <see cref="MaxSideError"/>。
        ///
        /// The downscaler's own resize: TextureResize.ResizeTexture works out the size (proportional, aligned to a
        /// multiple of 4), scales with a RenderTexture and writes a PNG, which is then loaded through FGL's cache.
        /// The two tests above write their own cache PNGs and skip this step; both aspect-ratio fixes (bc765bc,
        /// 746918a) were made here. The long side must equal the target, and neither side may be more than
        /// <see cref="MaxSideError"/> from the exact proportion.
        /// </summary>
        [Test]
        public static void DownscaledTextureKeepsSourceAspectRatio()
        {
            if (ExternalTextureToolActive) return;

            var resize = new TextureResize(FasterGameLoadingMod.Instance.CacheManager);
            var failures = new List<string>();
            foreach (var (width, height, target) in AspectRatioCases)
            {
                using var probe = new ResizeProbe(width, height);
                int hitsBefore = ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits;

                probe.Resize(resize, target);
                var texture = probe.Load();

                if (ModContentLoaderTexture2D_LoadTexture_Patch.cacheLoadHits != hitsBefore + 1)
                {
                    failures.Add(FormattableString.Invariant($"{width}x{height} to {target}: not served from the downscale cache ({texture.width}x{texture.height})"));
                    continue;
                }
                double scale = (double)target / Math.Max(width, height);
                double exactWidth = width * scale;
                double exactHeight = height * scale;
                if (Math.Max(texture.width, texture.height) != target
                    || Math.Abs(texture.width - exactWidth) > MaxSideError
                    || Math.Abs(texture.height - exactHeight) > MaxSideError)
                {
                    failures.Add(FormattableString.Invariant($"{width}x{height} to {target}: {texture.width}x{texture.height}, proportional {exactWidth:0.#}x{exactHeight:0.#}"));
                }
            }
            FglState.AssertNone(failures, "downscaled textures off their source's aspect ratio");
        }

        /// <summary>
        /// 降質工具在背景執行緒以 EncodeArrayToPNG 編碼讀回的像素；結果必須與同一份像素放進貼圖後以 EncodeToPNG 編碼的位元組完全相同，
        /// 否則快取檔的列序或色彩格式會與改成流水線之前不同。
        ///
        /// The downscaler encodes the read-back pixels with EncodeArrayToPNG on a background thread. The result
        /// must be byte-for-byte the same as EncodeToPNG on a texture holding the same pixels; otherwise the cache
        /// files' row order or colour format would differ from before the pipeline.
        /// </summary>
        [Test]
        public static void BackgroundPngEncodingMatchesEncodeToPng()
        {
            const int SourceWidth = 96, SourceHeight = 40, Width = 48, Height = 20;
            var source = new Texture2D(SourceWidth, SourceHeight, TextureFormat.RGBA32, mipChain: false);
            Texture2D reference = null;
            try
            {
                var pixels = new Color32[SourceWidth * SourceHeight];
                for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32((byte)i, (byte)(i / SourceWidth * 6), (byte)(255 - i), (byte)(128 + i % 128));
                source.SetPixels32(pixels);
                source.Apply();

                var resized = TextureResizer.ReadResizedPixels(source, Width, Height);
                var encoded = System.Threading.Tasks.Task.Run(() => TextureResizer.EncodePng(resized)).Result;

                reference = new Texture2D(Width, Height, TextureFormat.RGBA32, mipChain: false);
                reference.LoadRawTextureData(resized.Rgba);
                reference.Apply();
                Assert.That(Convert.ToBase64String(encoded)).Is.EqualTo(Convert.ToBase64String(reference.EncodeToPNG()));
            }
            finally
            {
                UnityEngine.Object.Destroy(source);
                if (reference != null) UnityEngine.Object.Destroy(reference);
            }
        }

        /// <summary>在隔離 session 的存檔資料夾產生一組原始貼圖與降質快取，結束時全部清掉。</summary>
        private sealed class Probe : IDisposable
        {
            public const string FileNameWithoutExtension = "FglDownscaleProbe";

            private readonly TextureCacheManager cacheManager = FasterGameLoadingMod.Instance.CacheManager;
            private readonly string cachePath;
            private Texture2D loaded;
            private Texture2D reference;

            public string OriginalPath { get; }

            public Probe(bool isUi)
            {
                var directory = Path.Combine(GenFilePaths.SaveDataFolderPath, "FglInGameTests", "Textures", isUi ? "UI" : "Things");
                Directory.CreateDirectory(directory);
                OriginalPath = Path.Combine(directory, FileNameWithoutExtension + ".png");
                WritePng(OriginalPath, OriginalSize, OriginalSize);

                cachePath = cacheManager.GetCachePath(OriginalPath);
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath));
                WritePng(cachePath, CachedSize, CachedSize);
                cacheManager.SetCacheEntry(OriginalPath, cachePath);
                LoadedTextureRegistry.Forget(OriginalPath);
            }

            public Texture2D Load() => loaded = LoadTexture(OriginalPath);

            /// <summary>對照組：直接載入快取 PNG 本身。它沒有快取項目，FGL 放行，由原版流程載入。</summary>
            public Texture2D LoadCacheFileDirectly() => reference = LoadTexture(cachePath);

            public void Dispose()
            {
                cacheManager.RemoveCachedTexturePath(OriginalPath);
                LoadedTextureRegistry.Forget(OriginalPath);
                LoadedTextureRegistry.Forget(cachePath);
                if (loaded != null) UnityEngine.Object.Destroy(loaded);
                if (reference != null) UnityEngine.Object.Destroy(reference);
                File.Delete(OriginalPath);
                File.Delete(cachePath);
            }

            internal static Texture2D LoadTexture(string path)
            {
                var file = AbstractFilesystem.GetDirectory(Path.GetDirectoryName(path)).GetFile(Path.GetFileName(path));
                var texture = ModContentLoader<Texture2D>.LoadTexture(file);
                if (texture == null) throw new AssertionException($"LoadTexture returned null for {path}");
                return texture;
            }

            internal static void WritePng(string path, int width, int height)
            {
                var texture = new Texture2D(width, height, TextureFormat.RGBA32, mipChain: false);
                try
                {
                    var pixels = new Color32[width * height];
                    for (int i = 0; i < pixels.Length; i++) pixels[i] = new Color32((byte)i, 128, 64, 255);
                    texture.SetPixels32(pixels);
                    texture.Apply();
                    File.WriteAllBytes(path, texture.EncodeToPNG());
                }
                finally
                {
                    UnityEngine.Object.Destroy(texture);
                }
            }
        }

        /// <summary>
        /// 一張非正方形的原始貼圖，交給降質工具真正的縮放流程；結束時清掉原圖、快取 PNG 與快取項目。
        /// A non-square source texture for the downscaler's real resize; disposing removes the source,
        /// the cache PNG and the cache entry.
        /// </summary>
        private sealed class ResizeProbe : IDisposable
        {
            private readonly TextureCacheManager cacheManager = FasterGameLoadingMod.Instance.CacheManager;
            private readonly string originalPath;
            private readonly int width;
            private readonly int height;
            private Texture2D loaded;

            public ResizeProbe(int width, int height)
            {
                this.width = width;
                this.height = height;
                var directory = Path.Combine(GenFilePaths.SaveDataFolderPath, "FglInGameTests", "Textures", "Things");
                Directory.CreateDirectory(directory);
                originalPath = Path.Combine(directory, FormattableString.Invariant($"FglResizeProbe_{width}x{height}.png"));
                Probe.WritePng(originalPath, width, height);
                Directory.CreateDirectory(Path.GetDirectoryName(cacheManager.GetCachePath(originalPath)));
                LoadedTextureRegistry.Forget(originalPath);
            }

            /// <summary>
            /// 以 BuildResizeCandidates 會產生的同一個候選呼叫 ResizeTexture（它自行從磁碟讀入原圖），
            /// 寫到正式快取目錄後登記項目，與重建交易升級後的結果相同。
            /// Calls ResizeTexture with the same candidate BuildResizeCandidates would make (it reads the source
            /// from disk itself), writes into the live cache folder and registers the entry, which matches what a
            /// promoted rebuild leaves behind.
            /// </summary>
            public void Resize(TextureResize resize, int targetSize)
            {
                var candidate = new TextureResize.TextureResizeCandidate
                {
                    path = originalPath,
                    targetSize = targetSize,
                    originalWidth = width,
                    originalHeight = height,
                };
                var cachePath = cacheManager.GetCachePath(originalPath);
                if (resize.ResizeTexture(candidate, cachePath))
                {
                    cacheManager.SetCacheEntry(originalPath, cachePath);
                }
            }

            public Texture2D Load() => loaded = Probe.LoadTexture(originalPath);

            public void Dispose()
            {
                // 快取路徑由原圖的路徑、大小與修改時間算出，必須在刪除原圖前取得。
                // The cache path comes from the source's path, size and modified time, so read it before
                // deleting the source.
                var cachePath = cacheManager.GetCachePath(originalPath);
                cacheManager.RemoveCachedTexturePath(originalPath);
                LoadedTextureRegistry.Forget(originalPath);
                if (loaded != null) UnityEngine.Object.Destroy(loaded);
                File.Delete(originalPath);
                File.Delete(cachePath);
            }
        }
    }
}
