using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Verse;

namespace FasterGameLoading
{
    /// <summary>
    /// 背景執行緒 JIT 預編譯器。
    /// 在背景執行緒中預先編譯所有第三方 Mod Assemblies 的方法與建構子，
    /// 減少遊戲啟動後半段執行 Harmony Patch 與靜態建構子時的 JIT 卡頓。
    /// </summary>
    public static class JITPrecompiler
    {
        /// <summary>
        /// 異步啟動背景預編譯。
        /// </summary>
        public static void StartPrecompilation()
        {
            Task.Run(() =>
            {
                try
                {
                    var stopwatch = Stopwatch.StartNew();
                    int compiledMethodsCount = 0;
                    int compiledTypesCount = 0;

                    var assemblies = AppDomain.CurrentDomain.GetAssemblies();

                    // 智慧優先級排序：上次被 Harmony Patch 的組件優先級最高，名稱含 "Patch" 的組件次之，其餘置後
                    List<string> patchedCopy = null;
                    lock (SessionCache.patchedAssembliesLock)
                    {
                        if (SessionCache.patchedAssembliesLastSession != null)
                        {
                            patchedCopy = new List<string>(SessionCache.patchedAssembliesLastSession);
                        }
                    }

                    var orderedAssemblies = assemblies.OrderByDescending(a =>
                    {
                        if (a == null) return -1;
                        try
                        {
                            var name = a.GetName().Name;
                            if (patchedCopy != null && patchedCopy.Contains(name))
                            {
                                return 100;
                            }
                            if (name.IndexOf("Patch", StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return 50;
                            }
                        }
                        catch
                        {
                            // 忽略個別動態組件可能拋出的異常
                        }
                        return 0;
                    }).ToList();

                    foreach (var assembly in orderedAssemblies)
                    {
                        if (assembly == null) continue;

                        try
                        {
                            var name = assembly.GetName().Name;
                            if (ShouldIgnoreAssembly(name))
                            {
                                continue;
                            }

                            Type[] types;
                            try
                            {
                                types = assembly.GetTypes();
                            }
                            catch (ReflectionTypeLoadException ex)
                            {
                                types = ex.Types;
                            }
                            catch
                            {
                                continue;
                            }

                            if (types == null) continue;

                            foreach (var type in types)
                            {
                                if (type == null) continue;

                                try
                                {
                                    // 遍歷編譯型別中所有宣告的方法
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

                                        try
                                        {
                                            RuntimeHelpers.PrepareMethod(method.MethodHandle);
                                            compiledMethodsCount++;
                                        }
                                        catch
                                        {
                                            // 忽略無法預編譯的特定泛型或 DynamicMethod
                                        }
                                    }

                                    // 遍歷編譯所有宣告的建構子
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

                                        try
                                        {
                                            RuntimeHelpers.PrepareMethod(ctor.MethodHandle);
                                            compiledMethodsCount++;
                                        }
                                        catch
                                        {
                                            // 忽略無法編譯的泛型建構子
                                        }
                                    }

                                    compiledTypesCount++;
                                }
                                catch
                                {
                                    // 忽略單一類型反射錯誤，確保遍歷不中斷
                                }
                            }
                        }
                        catch
                        {
                            // 忽略個別 Assembly 反射錯誤
                        }
                    }

                    stopwatch.Stop();
                    FGLLog.Message($"Background JIT pre-compilation complete in {stopwatch.ElapsedMilliseconds} ms. Compiled {compiledTypesCount} types, {compiledMethodsCount} methods.");
                }
                catch (Exception ex)
                {
                    FGLLog.Warning("Error during background JIT pre-compilation: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// 判斷是否應該忽略此 Assembly 的 JIT 預編譯。
        /// </summary>
        private static bool ShouldIgnoreAssembly(string name)
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
    }
}
