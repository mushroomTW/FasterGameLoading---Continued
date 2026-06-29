using Verse;
namespace FasterGameLoading
{
    /// <summary>
    /// Image Opt 相容性檢查器。
    /// </summary>
    public static class ImageOptCompat
    {
        private static bool? isActive;
        static ImageOptCompat()
        {
            CacheResetter.Register(() => isActive = null);
        }
        public static bool IsActive
        {
            get
            {
                if (!isActive.HasValue)
                {
                    isActive = IsModActive("dev.soeur.imageopt");
                }
                return isActive.Value;
            }
        }
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
