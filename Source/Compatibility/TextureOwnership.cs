using System;
using Verse;

namespace FasterGameLoading
{
    /// <summary>負責載入 Mod 貼圖的一方。</summary>
    public enum TextureOwner
    {
        /// <summary>原版載入流程，FGL 的降質快取與貼圖登記生效。</summary>
        Fgl,
        /// <summary>Image Opt 接手（DDS 快取）；FGL 的貼圖補丁完全讓開。</summary>
        ImageOpt,
        /// <summary>Graphics Settings+ 接手；FGL 的貼圖補丁完全讓開。</summary>
        GraphicsSettings,
    }

    /// <summary>
    /// 判斷目前由誰負責載入 Mod 貼圖。mod 清單在遊戲執行期間不會改變，第一次查詢成功後就定案，
    /// 貼圖補丁的熱路徑不必每張貼圖都查一次 ModsConfig。
    /// </summary>
    public static class TextureOwnership
    {
        /// <summary>Graphics Settings+ 的 Harmony ID；FGL 的 LoadTexture 補丁排在它之前。</summary>
        public const string GraphicsSettingsHarmonyId = "com.telefonmast.graphicssettings.rimworld.mod";

        internal const string ImageOptPackageId = "dev.soeur.imageopt";
        internal const string GraphicsSettingsPackageId = "Telefonmast.GraphicsSettings";

        private static TextureOwner? detected;

        /// <summary>目前負責載入 Mod 貼圖的一方；兩個外部工具同時啟用時以 Image Opt 為準。</summary>
        public static TextureOwner Current
        {
            get
            {
                if (detected is { } owner)
                {
                    return owner;
                }
                try
                {
                    if (IsActiveIgnoringSteamSuffix(ImageOptPackageId))
                    {
                        owner = TextureOwner.ImageOpt;
                    }
                    else if (IsActiveIgnoringSteamSuffix(GraphicsSettingsPackageId))
                    {
                        owner = TextureOwner.GraphicsSettings;
                    }
                    else
                    {
                        owner = TextureOwner.Fgl;
                    }
                }
                catch (Exception)
                {
                    // ModsConfig 尚未就緒：這次當作 FGL 自己載入，但不定案，下次再查。
                    return TextureOwner.Fgl;
                }
                detected = owner;
                return owner;
            }
        }

        /// <summary>
        /// 同一個 mod 同時有本機與 Workshop 副本時，Workshop 版的 PackageId 會帶 "_steam" 後綴，
        /// ModsConfig.IsActive 的精確比對會漏掉它；改以忽略後綴的查詢判斷。
        /// </summary>
        private static bool IsActiveIgnoringSteamSuffix(string packageId)
            => ModLister.GetActiveModWithIdentifier(packageId, ignorePostfix: true) != null;

        /// <summary>FGL 的降質快取、背景預讀與貼圖登記是否生效。</summary>
        public static bool FglOwnsTextureLoading => Current is TextureOwner.Fgl;

        /// <summary>測試用：直接指定擁有者；傳 null 回到自動偵測。</summary>
        internal static void OverrideForTests(TextureOwner? owner)
        {
            detected = owner;
        }
    }
}
