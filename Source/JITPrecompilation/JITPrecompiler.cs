using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 在背景執行緒上預熱已載入的模組組件，以減少首次使用時的 JIT 停頓。
    /// </summary>
    public static class JITPrecompiler
    {
        private const int DefaultInitialDelayMs = 250;
        private const int YieldEveryTypes = 32;

        private static int isPrecompilationRunning;

        internal static bool IsPrecompilationRunning => Volatile.Read(ref isPrecompilationRunning) == 1;

        internal struct JITPrecompilationStats
        {
            public int CompiledTypesCount;
            public int CompiledMethodsCount;
        }

        // 執行緒安全的工作佇列，存放主執行緒收集到的待編譯方法控制代碼
        private static readonly ConcurrentQueue<RuntimeMethodHandle> methodQueue = new ConcurrentQueue<RuntimeMethodHandle>();

        // 反射收集狀態
        private static List<Assembly> orderedAssemblies;
        private static int currentAssemblyIndex;
        private static Type[] currentTypes;
        private static int currentTypeIndex;
        private static volatile bool isCollectionStarted;
        private static volatile bool isCollectionFinished;
        private static int collectedTypesCount;
        private static int compiledMethodsCount;
        private static readonly Stopwatch updateStopwatch = new Stopwatch();

        /// <summary>
        /// 啟動一個背景 JIT 預熱工作。如果已有其他工作在執行中，則返回 false。
        /// </summary>
        public static bool StartPrecompilation()
        {
            if (Interlocked.CompareExchange(ref isPrecompilationRunning, 1, 0) != 0)
            {
                FGLLog.Message("Background JIT pre-compilation is already running; skipping duplicate request.");
                return false;
            }

            // 在主執行緒初始化待預編譯的組件清單並進行排序
            try
            {
                var assemblies = AppDomain.CurrentDomain.GetAssemblies();
                var patchedAssemblies = GetPatchedAssembliesSnapshot();
                orderedAssemblies = assemblies
                    .Where(IsPrecompilationCandidate)
                    .OrderByDescending(a => GetAssemblyPrioritySafe(a, patchedAssemblies))
                    .ToList();
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Failed to initialize assemblies for JIT precompilation: " + ex.Message);
                Volatile.Write(ref isPrecompilationRunning, 0);
                return false;
            }

            currentAssemblyIndex = 0;
            currentTypes = null;
            currentTypeIndex = 0;
            collectedTypesCount = 0;
            compiledMethodsCount = 0;

            // 清空佇列
            while (methodQueue.TryDequeue(out _)) { }

            // 標記收集開始與未結束
            isCollectionFinished = false;
            isCollectionStarted = true;

            // 啟動背景編譯 Task
            Task.Run(() =>
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();

                    // 稍微延遲以待主執行緒開始收集
                    Thread.Sleep(DefaultInitialDelayMs);

                    while (!isCollectionFinished || !methodQueue.IsEmpty)
                    {
                        if (methodQueue.TryDequeue(out var methodHandle))
                        {
                            try
                            {
                                RuntimeHelpers.PrepareMethod(methodHandle);
                                Interlocked.Increment(ref compiledMethodsCount);
                            }
                            catch
                            {
                                // 跳過編譯失敗的方法
                            }
                        }
                        else
                        {
                            // 佇列暫時空了，但收集尚未結束，稍作等待以避免 CPU 忙碌空轉
                            Thread.Sleep(5);
                        }
                    }

                    stopwatch.Stop();
                    FGLLog.Message($"Background JIT pre-compilation complete in {stopwatch.ElapsedMilliseconds} ms. Compiled {collectedTypesCount} types, {compiledMethodsCount} methods.");
                }
                catch (Exception ex)
                {
                    FGLLog.Warning("Error during background JIT pre-compilation execution: " + ex.Message);
                }
                finally
                {
                    Volatile.Write(ref isPrecompilationRunning, 0);
                    isCollectionStarted = false;
                    isCollectionFinished = true;
                }
            });

            return true;
        }

        internal static bool StartPrecompilation(Func<JITPrecompilationStats> compileAction, int initialDelayMs)
        {
            if (Interlocked.CompareExchange(ref isPrecompilationRunning, 1, 0) != 0)
            {
                FGLLog.Message("Background JIT pre-compilation is already running; skipping duplicate request.");
                return false;
            }

            Task.Run(() =>
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    if (initialDelayMs > 0)
                    {
                        Thread.Sleep(initialDelayMs);
                    }

                    var stats = compileAction();
                    stopwatch.Stop();

                    FGLLog.Message($"Background JIT pre-compilation complete in {stopwatch.ElapsedMilliseconds} ms. Compiled {stats.CompiledTypesCount} types, {stats.CompiledMethodsCount} methods.");
                }
                catch (Exception ex)
                {
                    FGLLog.Warning("Error during background JIT pre-compilation: " + ex.Message);
                }
                finally
                {
                    Volatile.Write(ref isPrecompilationRunning, 0);
                }
            });

            return true;
        }

        /// <summary>
        /// 由主執行緒每影格調用，逐步且限時地對組件與類型進行反射篩選，
        /// 並將待編譯方法的 MethodHandle 丟入佇列中。
        /// </summary>
        public static void UpdateReflectionCollection()
        {
            if (!isCollectionStarted || isCollectionFinished)
            {
                return;
            }

            updateStopwatch.Restart();

            try
            {
                // 每影格最多佔用 2ms 的主執行緒時間
                while (updateStopwatch.ElapsedMilliseconds < 2)
                {
                    // 1. 如果當前類型的陣列為空，或者已經遍歷完畢，則載入下一個組件的類型
                    if (currentTypes == null || currentTypeIndex >= currentTypes.Length)
                    {
                        if (orderedAssemblies == null || currentAssemblyIndex >= orderedAssemblies.Count)
                        {
                            // 所有組件掃描完畢
                            isCollectionFinished = true;
                            break;
                        }

                        var assembly = orderedAssemblies[currentAssemblyIndex];
                        currentAssemblyIndex++;
                        currentTypes = GetLoadableTypes(assembly);
                        currentTypeIndex = 0;
                        continue;
                    }

                    // 2. 獲取當前需要反射的 Type
                    var type = currentTypes[currentTypeIndex];
                    currentTypeIndex++;

                    if (type == null)
                    {
                        continue;
                    }

                    try
                    {
                        // 收集 Methods
                        var methods = type.GetMethods(BindingFlags.DeclaredOnly |
                                                      BindingFlags.Public |
                                                      BindingFlags.NonPublic |
                                                      BindingFlags.Instance |
                                                      BindingFlags.Static);
                        foreach (var method in methods)
                        {
                            if (method != null && !method.IsAbstract && !method.ContainsGenericParameters)
                            {
                                methodQueue.Enqueue(method.MethodHandle);
                            }
                        }

                        // 收集 Constructors
                        var constructors = type.GetConstructors(BindingFlags.DeclaredOnly |
                                                                BindingFlags.Public |
                                                                BindingFlags.NonPublic |
                                                                BindingFlags.Instance |
                                                                BindingFlags.Static);
                        foreach (var ctor in constructors)
                        {
                            if (ctor != null && !ctor.ContainsGenericParameters)
                            {
                                methodQueue.Enqueue(ctor.MethodHandle);
                            }
                        }

                        collectedTypesCount++;
                    }
                    catch
                    {
                        // 忽略個別型別反射失敗的異常
                    }
                }
            }
            catch (Exception ex)
            {
                FGLLog.Warning("Error during JIT reflection collection: " + ex.Message);
                // 發生意外異常時，安全地結束收集，避免背景執行緒無限等待
                isCollectionFinished = true;
            }
            finally
            {
                updateStopwatch.Stop();
            }
        }

        private static HashSet<string> GetPatchedAssembliesSnapshot()
        {
            lock (SessionCache.patchedAssembliesLock)
            {
                if (SessionCache.patchedAssembliesLastSession == null)
                {
                    return null;
                }

                return new HashSet<string>(SessionCache.patchedAssembliesLastSession, StringComparer.OrdinalIgnoreCase);
            }
        }

        private static Type[] GetLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsPrecompilationCandidate(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic)
            {
                return false;
            }

            string location;
            try
            {
                location = assembly.Location;
            }
            catch (NotSupportedException)
            {
                return false;
            }

            if (string.IsNullOrEmpty(location))
            {
                return false;
            }

            try
            {
                return !ShouldIgnoreAssembly(assembly.GetName().Name);
            }
            catch
            {
                return false;
            }
        }

        internal static bool ShouldIgnoreAssembly(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;

            if (FGLConsts.IgnoredAssemblyExactNames.Contains(name))
            {
                return true;
            }

            foreach (var prefix in FGLConsts.IgnoredAssemblyPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        internal static int GetAssemblyPriority(string name, ISet<string> patchedAssemblies)
        {
            if (string.IsNullOrEmpty(name))
            {
                return -1;
            }

            if (patchedAssemblies != null && patchedAssemblies.Contains(name))
            {
                return 100;
            }

            if (name.IndexOf("Patch", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return 50;
            }

            return 0;
        }

        private static int GetAssemblyPrioritySafe(Assembly assembly, ISet<string> patchedAssemblies)
        {
            try
            {
                return GetAssemblyPriority(assembly.GetName().Name, patchedAssemblies);
            }
            catch
            {
                return -1;
            }
        }

        private static int PrepareMethods(Type type)
        {
            int compiledMethodsCount = 0;
            var methods = type.GetMethods(BindingFlags.DeclaredOnly |
                                          BindingFlags.Public |
                                          BindingFlags.NonPublic |
                                          BindingFlags.Instance |
                                          BindingFlags.Static);
            foreach (var method in methods)
            {
                if (method == null || method.IsAbstract || method.ContainsGenericParameters)
                {
                    continue;
                }

                if (TryPrepareMethod(method))
                {
                    compiledMethodsCount++;
                }
            }

            return compiledMethodsCount;
        }

        private static int PrepareConstructors(Type type)
        {
            int compiledMethodsCount = 0;
            var constructors = type.GetConstructors(BindingFlags.DeclaredOnly |
                                                    BindingFlags.Public |
                                                    BindingFlags.NonPublic |
                                                    BindingFlags.Instance |
                                                    BindingFlags.Static);
            foreach (var ctor in constructors)
            {
                if (ctor == null || ctor.ContainsGenericParameters)
                {
                    continue;
                }

                if (TryPrepareMethod(ctor))
                {
                    compiledMethodsCount++;
                }
            }

            return compiledMethodsCount;
        }

        private static bool TryPrepareMethod(MethodBase method)
        {
            try
            {
                RuntimeHelpers.PrepareMethod(method.MethodHandle);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal static void ResetStateForTests()
        {
            Volatile.Write(ref isPrecompilationRunning, 0);
        }
    }
}
