using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 與外部貼圖載入器模組的相容性檢查器。
    /// </summary>
    public static class DDSCompat
    {
        public const string GraphicsSetterHarmonyId = "com.telefonmast.graphicssettings.rimworld.mod";

        private static bool? isImageOptActive;
        private static bool? isGraphicsSetterActive;

        /// <summary>
        /// 取得 Image Opt 是否為啟用狀態。
        /// </summary>
        public static bool IsImageOptActive
        {
            get
            {
                if (!isImageOptActive.HasValue)
                {
                    isImageOptActive = IsModActive("dev.soeur.imageopt");
                }
                return isImageOptActive.Value;
            }
        }

        /// <summary>
        /// 取得 Graphics Settings+ / GraphicsSetter 是否為啟用狀態。
        /// </summary>
        public static bool IsGraphicsSetterActive
        {
            get
            {
                if (!isGraphicsSetterActive.HasValue)
                {
                    isGraphicsSetterActive = IsModActive("Telefonmast.GraphicsSettings");
                }
                return isGraphicsSetterActive.Value;
            }
        }

        /// <summary>
        /// 是否應停用 FGL 自身的降質貼圖替換，讓外部 DDS/mipmap 載入器優先處理。
        /// </summary>
        public static bool ShouldBypassTextureReplacement => IsGraphicsSetterActive;

        private static bool IsModActive(string packageId)
        {
            try
            {
                return ModsConfig.IsActive(packageId);
            }
            catch
            {
                return false;
            }
        }
    }
}
