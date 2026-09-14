using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DSHDesktop.Tests;

/// <summary>
/// <c>PnpmSupport.StoreMajor</c> 的 store 版本推断回归测试。
///
/// 背景（真实缺陷，2026-09-14 由评审发现）：该函数原用 <c>store[\\/]v(?&lt;major&gt;\d{1,2})\b</c>
/// 解析 <c>node_modules/.modules.yaml</c>。正则字符类里的 <c>\\</c> 是<b>被转义的一个反斜杠</b>，
/// 即只匹配「一个反斜杠或一个斜杠」；但 .modules.yaml 里的 storeDir 是 JSON 转义后的路径，
/// Windows 上 store 与版本号之间是<b>两个</b>反斜杠字节（实测 <c>5C 5C</c>）。于是该模式在
/// Windows 上对任何版本都恒不匹配，函数静默回落到 <c>DefaultMajor</c>：
///   - store 恰好是 v11 时看不出问题（与默认值相同）；
///   - store 是 v10 时会被当成 v11，正是 <c>ERR_PNPM_UNEXPECTED_STORE</c> 的成因。
///
/// 本测试**从源码里抠出模式字符串**再匹配（而不是把模式抄进测试），因此若有人把模式改回
/// 单分隔符形式，测试会真的变红——而不是继续拿着一份与实现脱节的副本自证正确。
/// </summary>
public class PnpmStoreMajorTests
{
    /// <summary>取出 StoreMajor 里那个 verbatim 正则字符串（<c>@"..."</c> 内无反斜杠转义语义）。</summary>
    private static string ExtractPattern()
    {
        var path = SourceScanner.ProductFile("PnpmSupport.cs");
        var src = File.ReadAllText(path);
        var at = src.IndexOf("Regex.Match(", StringComparison.Ordinal);
        Assert.True(at >= 0, "PnpmSupport.cs 里找不到 Regex.Match 调用");
        var tail = src.Substring(at);
        var start = tail.IndexOf("@\"", StringComparison.Ordinal);
        Assert.True(start >= 0, "Regex.Match 之后找不到 verbatim 字符串");
        start += 2;
        var end = tail.IndexOf('"', start);
        Assert.True(end > start, "verbatim 字符串未闭合");
        return tail.Substring(start, end - start);
    }

    private static string MajorOf(string pattern, string modulesYaml)
    {
        var m = Regex.Match(modulesYaml, pattern);
        return m.Success ? m.Groups["major"].Value : "(不匹配)";
    }

    /// <summary>模式必须能接受 1~2 个分隔符，否则下面每一类真实输入都会失败。</summary>
    [Fact]
    public void Pattern_AcceptsOneOrTwoSeparators()
    {
        var pattern = ExtractPattern();
        Assert.Contains(@"[\\/]{1,2}", pattern, StringComparison.Ordinal);
    }

    /// <summary>Windows 上 JSON 转义产生的双反斜杠（本次缺陷的直接成因）。</summary>
    [Theory]
    [InlineData(11)]
    [InlineData(10)]
    [InlineData(9)]
    public void DoubleBackslash_StoreDir_IsParsed(int major)
    {
        // 落盘内容里是 \\（JSON 转义），C# 读回后是两个反斜杠字符
        var yaml = "{ \"storeDir\": \"C:\\\\Users\\\\u\\\\AppData\\\\Local\\\\pnpm\\\\store\\\\v" + major + "\" }";
        Assert.Equal(major.ToString(), MajorOf(ExtractPattern(), yaml));
    }

    /// <summary>正斜杠形态（Linux / 非 Windows 写入方）。</summary>
    [Theory]
    [InlineData(11)]
    [InlineData(10)]
    public void ForwardSlash_StoreDir_IsParsed(int major)
    {
        var yaml = "{ \"storeDir\": \"/home/u/.local/share/pnpm/store/v" + major + "\" }";
        Assert.Equal(major.ToString(), MajorOf(ExtractPattern(), yaml));
    }

    /// <summary>单反斜杠形态（部分写入方不转义）；修复不能把它弄坏。</summary>
    [Fact]
    public void SingleBackslash_StoreDir_IsStillParsed()
    {
        var yaml = "{ \"storeDir\": \"C:" + "\\" + "Users" + "\\" + "u" + "\\" + "pnpm" + "\\" + "store" + "\\" + "v10\" }";
        Assert.Equal("10", MajorOf(ExtractPattern(), yaml));
    }

    /// <summary>混合分隔符（真实夹具里出现过 <c>AppData/Local/pnpm/store\\v11</c>）。</summary>
    [Fact]
    public void MixedSeparators_StoreDir_IsParsed()
    {
        var yaml = "{ \"storeDir\": \"C:\\\\Users\\\\u\\\\AppData/Local/pnpm/store\\\\v11\" }";
        Assert.Equal("11", MajorOf(ExtractPattern(), yaml));
    }

    /// <summary>不含 store 关键字时不得误报，必须走到回退分支。</summary>
    [Fact]
    public void MissingStoreKeyword_DoesNotMatch()
    {
        var yaml = "{ \"virtualStoreDir\": \"C:\\\\u\\\\.pnpm\" }";
        Assert.Equal("(不匹配)", MajorOf(ExtractPattern(), yaml));
    }

    /// <summary>
    /// 与真实工作区的 <c>.modules.yaml</c> 对拍（存在才算，缺失就跳过）：
    /// 本机 profile 的 storeDir 目前是 v11，若能读到，模式必须解析出 11。
    /// </summary>
    [Fact]
    public void RealProfileModulesYaml_IsParsed_WhenPresent()
    {
        var profiles = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dsh", "profiles");
        if (!Directory.Exists(profiles)) return;   // 干净环境（CI runner）没有 profile，跳过

        var candidates = Directory.EnumerateFiles(profiles, ".modules.yaml", SearchOption.AllDirectories)
            .Where(p => p.Contains("node_modules", StringComparison.OrdinalIgnoreCase))
            .Take(5)
            .ToList();
        if (candidates.Count == 0) return;

        var pattern = ExtractPattern();
        foreach (var file in candidates)
        {
            var text = File.ReadAllText(file);
            var m = Regex.Match(text, pattern);
            Assert.True(m.Success, "真实 .modules.yaml 未能解析出 store 版本：" + file);
            var major = int.Parse(m.Groups["major"].Value);
            Assert.InRange(major, 8, 20);
        }
    }
}
