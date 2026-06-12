using NUnit.Framework;
using System;
using System.IO;
using System.Reflection;

namespace FasterGameLoading.Tests
{
    /// <summary>
    /// 單元測試初始化設定。
    /// 透過 AssemblyResolve 事件，在單元測試執行期間自動從本地 RimWorld 安裝目錄載入 Assembly-CSharp.dll 與其相依的 Unity DLL，
    /// 以解決單元測試執行時的 FileNotFoundException。
    /// </summary>
    [SetUpFixture]
    public class TestSetup
    {
        [OneTimeSetUp]
        public void RunBeforeAnyTests()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
            {
                var assemblyName = new AssemblyName(args.Name).Name;
                
                // 本地 RimWorld Managed 檔案夾路徑；可透過環境變數 RIMWORLD_MANAGED_DIR 覆寫，
                // 方便在不同機器或 CI 環境中執行測試而不需修改程式碼。
                var managedDir = Environment.GetEnvironmentVariable("RIMWORLD_MANAGED_DIR")
                    ?? @"c:\Program Files (x86)\Steam\steamapps\common\RimWorld\RimWorldWin64_Data\Managed";
                var path = Path.Combine(managedDir, assemblyName + ".dll");
                
                if (File.Exists(path))
                {
                    try
                    {
                        return Assembly.LoadFrom(path);
                    }
                    catch
                    {
                        // 忽略個別檔案載入失敗
                    }
                }
                return null;
            };
        }
    }
}
