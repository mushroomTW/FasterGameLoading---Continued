using System;
using System.Diagnostics;
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
                    foreach (var assembly in assemblies)
                    {
                        if (assembly == null) continue;

                        try
                        {
                            var name = assembly.GetName().Name;
                            // 排除系統元件、Unity 原生元件、Harmony 元件以及遊戲本體，
                            // 專注預熱載入的第三方 Mod 程式集。
                            if (name.StartsWith("System", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase) ||
                                name.StartsWith("Unity", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("Assembly-CSharp", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("0Harmony", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("Mono.Cecil", StringComparison.OrdinalIgnoreCase) ||
                                name.Equals("Anonymously Hosted DynamicMethods Assembly", StringComparison.Ordinal))
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
    }
}
