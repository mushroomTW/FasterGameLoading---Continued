using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using HarmonyLib;
using NUnit.Framework;
using RimWorld;
using UnityEngine;
using Verse;

namespace FasterGameLoading.Tests;
[TestFixture]
public class UtilsTests
{
    private static T Uninitialized<T>() => (T)FormatterServices.GetUninitializedObject(typeof(T));

    [Test]
    public void TestNormalizePath_WithNull_ReturnsNull()
    {
        string input = null;
        string result = input.NormalizePath();
        Assert.IsNull(result);
    }

    [Test]
    public void TestNormalizePath_WithWindowsBackslashes_NormalizesToForwardSlashes()
    {
        string input = @"C:\Program Files (x86)\RimWorld\Mods\Textures\UI\Icon.png";
        string expected = "C:/Program Files (x86)/RimWorld/Mods/Textures/UI/Icon.png";
        string result = input.NormalizePath();
        Assert.AreEqual(expected, result);
    }

    [Test]
    public void TestNormalizePath_WithMixedSlashes_NormalizesToForwardSlashes()
    {
        string input = "C:/Program Files (x86)\\RimWorld/Mods\\Textures/UI/Icon.png";
        string expected = "C:/Program Files (x86)/RimWorld/Mods/Textures/UI/Icon.png";
        string result = input.NormalizePath();
        Assert.AreEqual(expected, result);
    }
}
