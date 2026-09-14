using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DSHDesktop.Tests;

/// <summary>
/// 结构测试（源码扫描）：<c>TerminalScreen</c> 公开的**每个**状态访问器，都必须在产品代码里有
/// 至少一处读取方；否则必须进下面的 allow-list 并写明理由。
///
/// 为什么这条契约值得锁（对应 round-8 的 O3 / 清单 B18）：改动前有 9 个模式位只写不读——
/// 解析器老老实实记录 <c>DECSET 2026</c>（同步输出）、<c>1005/1015/1016</c>（鼠标编码）、
/// <c>1047/1049</c>（备用屏幕），渲染层却从不消费，等于"应用以为终端支持，实际没有"。
/// 这类"死状态"不会报错，只会安静地表现为功能缺失，靠人读代码很难发现。
///
/// allow-list 的每一项都是**显式取舍**，不是遗漏：解析器内部用的是字段，公开访问器是模式查询面。
/// </summary>
public class TerminalStateConsumptionTests
{
    /// <summary>解析器内部状态：字段参与解析逻辑，公开访问器暂无产品读取方（测试会读）。</summary>
    private static readonly Dictionary<string, string> ParserInternalOnly = new()
    {
        ["AutoWrap"] = "DECAWM：解析器在 PutRune 里读 _autoWrap（自动换行语义已实现）；访问器是模式查询面，由测试覆盖",
        ["OriginMode"] = "DECOM：解析器在 ClampVertical 里读 _originMode（原点模式已实现）；访问器暂无读取方，保留为模式查询面",
        ["InsertMode"] = "IRM：解析器在 PutRune 里读 _insertMode（插入模式已实现）；访问器由测试覆盖",
        ["CursorY"] = "与 CursorX 对称的查询面；渲染走 CursorViewportRow（它已把回滚偏移算进去），测试直接读 CursorY",
        ["Version"] = "解析状态计数器：目前只被测试与调试读取；留作将来「按版本跳过重绘」的挂点",
    };

    [Fact]
    public void EveryStateAccessor_HasAProductConsumer_OrIsAllowListed()
    {
        var screenPath = SourceScanner.ProductFile("Terminal", "TerminalScreen.cs");
        var screenCode = SourceScanner.StrippedSource(screenPath);

        // 只取实例状态访问器：排除 static / event / const 成员
        var accessors = Regex
            .Matches(screenCode, @"public\s+(?!static|event|const)(?:bool|int|uint|byte|char|string|object)\??\s+(?<name>\w+)\s*(?:=>|\{)")
            .Select(m => m.Groups["name"].Value)
            .Distinct()
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // 扫描器自身失效时必须失败，而不是"一个都没解析到所以全绿"
        Assert.True(accessors.Count >= 20, $"只解析出 {accessors.Count} 个状态访问器，扫描器需要更新");
        Assert.Contains("SynchronizedOutput", accessors);
        Assert.Contains("MouseSgrPixels", accessors);

        // 消费方 = 产品代码里除 TerminalScreen 之外的文件（同样只看"只剩代码"的投影）
        var consumerCode = SourceScanner.ProductSources()
            .Where(p => !p.EndsWith("TerminalScreen.cs", StringComparison.Ordinal))
            .Select(SourceScanner.StrippedSource)
            .ToList();

        var orphans = accessors
            .Where(name => !ParserInternalOnly.ContainsKey(name))
            .Where(name => !consumerCode.Any(text => Regex.IsMatch(text, @"\b" + Regex.Escape(name) + @"\b")))
            .ToList();

        Assert.Empty(orphans);

        // allow-list 也要保持诚实：列进去的名字必须真的还存在
        foreach (var name in ParserInternalOnly.Keys)
            Assert.Contains(name, accessors);
    }

    /// <summary>O3 的具体契约：渲染层必须消费同步输出与三套鼠标编码模式。</summary>
    [Theory]
    [InlineData("SynchronizedOutput")]     // DECSET 2026：组帧期间不重绘
    [InlineData("UsingAlternateScreen")]   // DECSET 1047/1049：备用屏幕下不浏览主屏回滚
    [InlineData("MouseUtf8")]              // DECSET 1005
    [InlineData("MouseUrxvt")]             // DECSET 1015
    [InlineData("MouseSgrPixels")]         // DECSET 1016
    public void RendererConsumesTheModeBit(string modeBit)
    {
        var viewPath = SourceScanner.ProductFile("Terminal", "TerminalView.cs");
        var viewCode = SourceScanner.StrippedSource(viewPath);

        Assert.Matches(@"\b" + modeBit + @"\b", viewCode);
    }
}
