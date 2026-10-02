using System;
using HarmonyLib;

namespace FasterGameLoading
{
    /// <summary>
    /// 讓 FGL 提早內容載入使用 ImageOpt 同步路徑，避免提早啟動原生佇列。
    /// </summary>
    internal static class ImageOptEarlyLoadCoordinator
    {
        private const string TextureLoadPatchTypeName = "ImageOpt.TextureLoadPatch";
        private const string StartedFieldName = "Started";
        private const string FailOpenMessage = "ImageOpt Early Loading synchronization could not be enabled. Early Loading remains active, but ImageOpt missing-ID errors may recur:";

        private static Func<bool> getStarted;
        private static Action<bool> setStarted;
        private static Action<string, Exception> logWarning = (message, ex) => FGLLog.Warning(message, ex);

        private static bool installed;
        private static bool installAttempted;

        internal static bool IsInstalled => installed;

        internal static void TryInstall()
        {
            if (installAttempted) return;
            installAttempted = true;

            if (Environment.OSVersion.Platform is not PlatformID.Win32NT || TextureOwnership.Current is not TextureOwner.ImageOpt) return;

            try
            {
                var textureLoadPatch = AccessTools.TypeByName(TextureLoadPatchTypeName);
                var startedField = AccessTools.Field(textureLoadPatch, StartedFieldName);
                if (startedField == null || startedField.FieldType != typeof(bool))
                {
                    throw new MissingMemberException("ImageOpt TextureLoadPatch.Started was not found.");
                }

                getStarted = () => (bool)startedField.GetValue(null);
                setStarted = v => startedField.SetValue(null, v);
                installed = true;
            }
            catch (Exception ex)
            {
                installed = false;
                logWarning(FailOpenMessage, ex);
            }
        }

        /// <summary>
        /// 讓 FGL 直接觸發的 ReloadContentInt 暫時走 ImageOpt 同步路徑。
        /// 非重入：正式路徑為單層 using，每個 scope 快照進入時的旗標並在 Dispose 還原。
        /// </summary>
        internal static IDisposable EnterEarlyLoadSyncScope()
        {
            if (!installed || getStarted == null || setStarted == null) return new SyncScope(startedFlagSet: false);

            bool prior;
            try
            {
                prior = getStarted();
            }
            catch
            {
                return new SyncScope(startedFlagSet: false);
            }

            if (prior) return new SyncScope(startedFlagSet: false);

            try
            {
                setStarted(true);
            }
            catch
            {
                return new SyncScope(startedFlagSet: false);
            }

            return new SyncScope(startedFlagSet: true);
        }

        private sealed class SyncScope : IDisposable
        {
            private readonly bool startedFlagSet;
            private bool disposed;

            public SyncScope(bool startedFlagSet)
            {
                this.startedFlagSet = startedFlagSet;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                if (!startedFlagSet) return;
                try
                {
                    setStarted(false);
                }
                catch
                {
                    // 還原旗標屬盡力而為，忽略版本異動導致的例外。
                }
            }
        }

        #region 測試支援
        internal static void ConfigureForTests(
            Func<bool> startedGetter,
            Action<bool> startedSetter,
            bool enabled = true)
        {
            getStarted = startedGetter;
            setStarted = startedSetter;
            installed = enabled;
        }

        internal static void ReportInstallFailureForTests(Exception ex)
        {
            installed = false;
            logWarning(FailOpenMessage, ex);
        }

        internal static void SetWarningSinkForTests(Action<string, Exception> sink)
        {
            logWarning = sink ?? ((message, ex) => { });
        }

        internal static void ResetTestConfiguration()
        {
            installed = false;
            getStarted = null;
            setStarted = null;
            logWarning = (message, ex) => FGLLog.Warning(message, ex);
        }
        #endregion
    }
}
