using System;
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

        /// <summary>
        /// 啟動一個背景 JIT 預熱工作。如果已有其他工作在執行中，則返回 false。
        /// </summary>
        public static bool StartPrecompilation()
        {
            return StartPrecompilation(CompileLoadedAssemblies, DefaultInitialDelayMs);
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

        private static JITPrecompilationStats CompileLoadedAssemblies()
        {
            var stats = new JITPrecompilationStats();
            var assemblies = AppDomain.CurrentDomain.GetAssemblies();
            var patchedAssemblies = GetPatchedAssembliesSnapshot();
            var orderedAssemblies = assemblies
                .Where(IsPrecompilationCandidate)
                .OrderByDescending(a => GetAssemblyPrioritySafe(a, patchedAssemblies))
                .ToList();

            int processedTypesSinceYield = 0;
            foreach (var assembly in orderedAssemblies)
            {
                try
                {
                    var types = GetLoadableTypes(assembly);
                    if (types == null) continue;

                    foreach (var type in types)
                    {
                        if (type == null) continue;

                        try
                        {
                            stats.CompiledMethodsCount += PrepareMethods(type);
                            stats.CompiledMethodsCount += PrepareConstructors(type);
                            stats.CompiledTypesCount++;

                            processedTypesSinceYield++;
                            if (processedTypesSinceYield >= YieldEveryTypes)
                            {
                                processedTypesSinceYield = 0;
                                Thread.Yield();
                            }
                        }
                        catch
                        {
                            // 某些模組類型在反射成員時會拋出異常；跳過它們並繼續預熱其餘部分。
                        }
                    }
                }
                catch
                {
                    // 防止單個有問題的組件使整個背景預熱失效。
                }
            }

            return stats;
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
