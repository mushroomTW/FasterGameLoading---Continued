using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// Graphics Settings+ / GraphicsSetter 相容性檢查器。
    /// </summary>
    public static class GraphicsSettingsCompat
    {
        public const string HarmonyId = "com.telefonmast.graphicssettings.rimworld.mod";

        private static bool? isActive;

        static GraphicsSettingsCompat()
        {
            CacheResetter.Register(() => isActive = null);
        }

        public static bool IsActive
        {
            get
            {
                if (!isActive.HasValue)
                {
                    isActive = IsModActive("Telefonmast.GraphicsSettings");
                }
                return isActive.Value;
            }
        }

        /// <summary>
        /// 是否應停用 FGL 自身的降質貼圖替換，讓外部 DDS/mipmap 載入器優先處理。
        /// </summary>
        public static bool ShouldBypassTextureReplacement => IsActive;

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
