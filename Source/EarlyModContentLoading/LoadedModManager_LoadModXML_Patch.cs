using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// LoadAllActiveMods 在 CreateModClasses（所有 Mod 建構子）之後才呼叫 LoadModXML，
    /// 以它作為「所有 mod 的 Harmony patch 都已套用」的訊號，讓提早載入從此開始。
    /// 無法直接 patch CreateModClasses：FGL 的 PatchAll 就是在它執行期間呼叫的，
    /// 對正在執行中的方法加的 postfix 不會對這一次呼叫生效。
    /// 不以 [HarmonyPatch] 標註：由 <see cref="HyperdriveCompat.PatchLoadModXML"/> 手動套用，
    /// 以便在 Hyperdrive 尚未建構時延後套用。
    /// </summary>
    public static class LoadedModManager_LoadModXML_Patch
    {
        public static void Prefix()
        {
            EarlyModContentLoader.ModClassesCreated = true;
        }
    }
}
