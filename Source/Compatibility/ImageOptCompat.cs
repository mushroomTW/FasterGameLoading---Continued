using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 與 Image Opt (dev.soeur.imageopt) 模組的相容性檢查器。
    /// </summary>
    public static class ImageOptCompat
    {
        private static bool? isImageOptActive;

        /// <summary>
        /// 取得 Image Opt 是否為啟用狀態。
        /// </summary>
        public static bool IsImageOptActive
        {
            get
            {
                if (!isImageOptActive.HasValue)
                {
                    isImageOptActive = ModsConfig.IsActive("dev.soeur.imageopt");
                }
                return isImageOptActive.Value;
            }
        }
    }
}
