using System.Collections.Generic;
using System.Linq;
using DSHDesktop.Terminal;
using Xunit;

namespace DSHDesktop.Tests;

/// <summary>
/// <c>Terminal/TerminalScreen.cs</c>（VT 解析 + 屏幕缓冲，1357 行）的行为契约。
/// 它是内嵌终端里最复杂、也最容易在改动中回归的一块，且只依赖 BCL，因此不需要任何
/// 窗口/ConPTY 环境即可测试。
///
/// 覆盖范围：打印与光标、换行与回滚缓冲、擦除、SGR、宽字符与组合符、字符集、
/// 模式位（DECSET/DECRST）、备用屏幕、OSC（标题/链接/剪贴板/颜色查询）、
/// 以及写回终端的应答序列。
/// </summary>
public class TerminalScreenTests
{
    /// <summary>取某视口行的文本；宽字符右半格（'\0'）显示为 '·'，便于断言。</summary>
    private static string Text(TerminalScreen screen, int viewportRow, int scrollOffset = 0)
    {
        var line = screen.GetVisibleLine(viewportRow, scrollOffset);
        Assert.NotNull(line);
        return new string(line!.Select(c => c.Char == '\0' ? '·' : c.Char).ToArray());
    }

    private static TerminalCell[] Cells(TerminalScreen screen, int viewportRow, int scrollOffset = 0)
    {
        var line = screen.GetVisibleLine(viewportRow, scrollOffset);
        Assert.NotNull(line);
        return line!;
    }

    // ---------------------------------------------------------------- 打印与光标

    [Fact]
    public void Feed_PlainText_WritesCellsAndAdvancesCursor()
    {
        var s = new TerminalScreen(10, 3);

        s.Feed("abc");

        Assert.Equal("abc       ", Text(s, 0));
        Assert.Equal(3, s.CursorX);
        Assert.Equal(0, s.CursorY);
    }

    [Fact]
    public void CarriageReturn_ResetsColumnWithoutChangingRow()
    {
        var s = new TerminalScreen(10, 3);

        s.Feed("abc\rXY");

        Assert.Equal("XYc       ", Text(s, 0));
        Assert.Equal(2, s.CursorX);
    }

    [Fact]
    public void LineFeed_AdvancesRowWithoutCarriageReturn()
    {
        var s = new TerminalScreen(10, 3);

        s.Feed("a\nb");

        Assert.Equal("a         ", Text(s, 0));
        Assert.Equal(" b        ", Text(s, 1));
        Assert.Equal(1, s.CursorY);
    }

    [Fact]
    public void CrLf_MovesToStartOfNextLine()
    {
        var s = new TerminalScreen(10, 3);

        s.Feed("a\r\nb");

        Assert.Equal("b         ", Text(s, 1));
    }

    [Fact]
    public void Backspace_MovesLeftWithoutErasing()
    {
        var s = new TerminalScreen(10, 3);

        s.Feed("ab\bX");

        Assert.Equal('a', Cells(s, 0)[0].Char);
        Assert.Equal('X', Cells(s, 0)[1].Char);
    }

    [Fact]
    public void Tab_MovesToNextMultipleOfEight()
    {
        var s = new TerminalScreen(24, 2);

        s.Feed("\t");
        Assert.Equal(8, s.CursorX);

        s.Feed("\t");
        Assert.Equal(16, s.CursorX);
    }

    [Fact]
    public void Feed_EmptyString_DoesNotBumpVersion()
    {
        var s = new TerminalScreen(10, 2);
        var before = s.Version;

        s.Feed("");

        Assert.Equal(before, s.Version);
    }

    // ---------------------------------------------------------------- 换行与回滚缓冲

    [Fact]
    public void Scrollback_KeepsLinesScrolledOffTheTop()
    {
        var s = new TerminalScreen(5, 2);

        s.Feed("a\r\nb\r\nc\r\n");

        Assert.Equal(2, s.ScrollbackCount);
        Assert.Equal("c    ", Text(s, 0));              // 视口第一行
        Assert.Equal("b    ", Text(s, 0, scrollOffset: 1));
        Assert.Equal("a    ", Text(s, 0, scrollOffset: 2));
    }

    [Fact]
    public void CursorViewportRow_IsNegativeWhenScrolledOutOfView()
    {
        var s = new TerminalScreen(5, 2);
        s.Feed("a\r\nb\r\nc");

        Assert.Equal(1, s.ScrollbackCount);
        Assert.Equal(1, s.CursorViewportRow(0));
        Assert.Equal(-1, s.CursorViewportRow(1));
    }

    [Fact]
    public void AutoWrap_DefaultsToOn_AndWrapsToNextRow()
    {
        var s = new TerminalScreen(3, 3);

        s.Feed("abcd");

        Assert.True(s.AutoWrap);
        Assert.Equal("abc", Text(s, 0));
        Assert.Equal("d  ", Text(s, 1));
        Assert.Equal(1, s.CursorY);
    }

    [Fact]
    public void AutoWrap_Disabled_OverwritesLastColumn()
    {
        var s = new TerminalScreen(3, 2);

        s.Feed("\x1b[?7l");
        s.Feed("abcd");

        Assert.False(s.AutoWrap);
        Assert.Equal("abd", Text(s, 0));   // 'd' 覆盖 'c'，不换行
        Assert.Equal(0, s.CursorY);
    }

    // ---------------------------------------------------------------- 擦除

    [Fact]
    public void EraseInLine_Mode0_ClearsFromCursorRight()
    {
        var s = new TerminalScreen(6, 2);

        s.Feed("abcdef");
        s.Feed("\r");
        s.Feed("\x1b[3C");
        s.Feed("\x1b[K");

        Assert.Equal("abc   ", Text(s, 0));
    }

    [Fact]
    public void EraseInDisplay_Mode2_ClearsScreenButKeepsScrollback()
    {
        var s = new TerminalScreen(5, 2);
        s.Feed("a\r\nb\r\nc\r\n");
        Assert.Equal(2, s.ScrollbackCount);

        s.Feed("\x1b[2J");

        Assert.Equal("     ", Text(s, 0));
        Assert.Equal("     ", Text(s, 1));
        Assert.Equal(2, s.ScrollbackCount);
    }

    [Fact]
    public void EraseInDisplay_Mode3_ClearsScrollback()
    {
        var s = new TerminalScreen(5, 2);
        s.Feed("a\r\nb\r\nc\r\n");

        s.Feed("\x1b[3J");

        Assert.Equal(0, s.ScrollbackCount);
        Assert.Equal("c    ", Text(s, 0));
    }

    [Fact]
    public void Clear_ResetsScreenCursorAndScrollback()
    {
        var s = new TerminalScreen(5, 2);
        s.Feed("a\r\nb\r\nc\r\n");

        s.Clear();

        Assert.Equal(0, s.ScrollbackCount);
        Assert.Equal("     ", Text(s, 0));
        Assert.Equal(0, s.CursorX);
        Assert.Equal(0, s.CursorY);
    }

    // ---------------------------------------------------------------- 光标定位

    [Fact]
    public void Cup_IsOneBased_AndSetsBothCoordinates()
    {
        var s = new TerminalScreen(6, 3);

        s.Feed("\x1b[2;3H");

        Assert.Equal(1, s.CursorY);
        Assert.Equal(2, s.CursorX);

        s.Feed("X");
        Assert.Equal("  X   ", Text(s, 1));
    }

    [Fact]
    public void CursorVisibility_Decset25_Toggles()
    {
        var s = new TerminalScreen(6, 2);
        Assert.True(s.CursorVisible);

        s.Feed("\x1b[?25l");
        Assert.False(s.CursorVisible);

        s.Feed("\x1b[?25h");
        Assert.True(s.CursorVisible);
    }

    // ---------------------------------------------------------------- SGR

    [Fact]
    public void Sgr_PaletteColor_AppliesAndResets()
    {
        var s = new TerminalScreen(4, 2);

        s.Feed("\x1b[31mR\x1b[0mN");

        var cells = Cells(s, 0);
        Assert.NotEqual(TerminalScreen.DefaultFg, cells[0].Fg);
        Assert.Equal(TerminalScreen.DefaultFg, cells[1].Fg);
        Assert.Equal(0, cells[1].Flags);
    }

    [Fact]
    public void Sgr_Bold_SetsFlagBit()
    {
        var s = new TerminalScreen(4, 2);

        s.Feed("\x1b[1mB");

        Assert.NotEqual(0, Cells(s, 0)[0].Flags & TerminalCell.Bold);
    }

    [Fact]
    public void Sgr_EmptySequence_ResetsAttributes()
    {
        // ESC[m 等价于 ESC[0m：PowerShell/PSReadLine 大量使用，若当成未知参数忽略，
        // 颜色会一直延续到命令输出上（源码里有专门注释）。
        var s = new TerminalScreen(4, 2);

        s.Feed("\x1b[31m\x1b[mR");

        var cell = Cells(s, 0)[0];
        Assert.Equal(TerminalScreen.DefaultFg, cell.Fg);
        Assert.Equal(0, cell.Flags);
    }

    [Fact]
    public void Sgr_Truecolor_SetsExactArgb()
    {
        var s = new TerminalScreen(4, 2);

        s.Feed("\x1b[38;2;1;2;3mT");

        Assert.Equal(0xFF010203u, Cells(s, 0)[0].Fg);
    }

    // ---------------------------------------------------------------- 宽字符与组合符

    [Fact]
    public void WideChar_OccupiesTwoCells()
    {
        var s = new TerminalScreen(6, 2);

        s.Feed("中");

        var cells = Cells(s, 0);
        Assert.Equal('中', cells[0].Char);
        Assert.Equal(0, cells[0].Flags & TerminalCell.WideTrail);
        Assert.Equal('\0', cells[1].Char);
        Assert.NotEqual(0, cells[1].Flags & TerminalCell.WideTrail);
        Assert.Equal(2, s.CursorX);
    }

    [Fact]
    public void WideChar_InLastColumn_WrapsToNextRow()
    {
        var s = new TerminalScreen(4, 3);

        s.Feed("\x1b[4G");   // 光标到最后一列
        s.Feed("中");

        Assert.Equal(1, s.CursorY);
        Assert.Equal('中', Cells(s, 1)[0].Char);
    }

    [Fact]
    public void CombiningMark_AttachesToPreviousCell()
    {
        var s = new TerminalScreen(6, 2);

        s.Feed("e\u0301");   // e + 组合尖音符

        var cell = Cells(s, 0)[0];
        Assert.Equal('e', cell.Char);
        Assert.Equal('\u0301', cell.Comb);
        Assert.Equal(1, s.CursorX);
    }

    [Fact]
    public void DecSpecialGraphics_TranslatesMappedCharacters()
    {
        var s = new TerminalScreen(6, 2);

        s.Feed("\x1b(0q");   // ESC ( 0 → G0 选 DEC 特殊图形；'q' → '─'

        Assert.Equal('─', Cells(s, 0)[0].Char);
    }

    // ---------------------------------------------------------------- 模式位

    [Fact]
    public void MouseModes_TrackAndClear()
    {
        var s = new TerminalScreen(10, 2);

        s.Feed("\x1b[?1000h");
        Assert.Equal(1000, s.MouseMode);

        s.Feed("\x1b[?1006h");
        Assert.True(s.MouseSgr);

        s.Feed("\x1b[?1006l");
        Assert.False(s.MouseSgr);

        s.Feed("\x1b[?1000l");
        Assert.Equal(0, s.MouseMode);
    }

    [Fact]
    public void SynchronizedOutput_StateIsTracked()
    {
        // 注意：DECSET 2026（同步输出）的语义由 TerminalScreen 记录状态、
        // 由 TerminalView 在渲染侧消费（组帧期间不重绘 + 200ms 兜底）。
        // 本用例只锁定「解析器侧的记录不该被删掉」这一半契约，
        // 渲染侧消费另有结构测试（TerminalStateConsumptionTests）覆盖。
        var s = new TerminalScreen(10, 2);

        s.Feed("\x1b[?2026h");
        Assert.True(s.SynchronizedOutput);

        s.Feed("\x1b[?2026l");
        Assert.False(s.SynchronizedOutput);
    }

    [Fact]
    public void InsertMode_ShiftsExistingCellsRight()
    {
        var s = new TerminalScreen(4, 2);
        s.Feed("abc");
        s.Feed("\r");

        s.Feed("\x1b[4h");
        Assert.True(s.InsertMode);
        s.Feed("X");

        Assert.Equal("Xabc", Text(s, 0));   // 已有内容右移一格，右侧被截断
        s.Feed("\x1b[4l");
        Assert.False(s.InsertMode);
    }

    // ---------------------------------------------------------------- 备用屏幕

    [Fact]
    public void AlternateScreen_1049_SwapsAndRestoresMainContent()
    {
        var s = new TerminalScreen(8, 2);
        s.Feed("MAIN");

        s.Feed("\x1b[?1049h");
        Assert.True(s.UsingAlternateScreen);
        s.Feed("ALT");
        Assert.Equal("ALT     ", Text(s, 0));

        s.Feed("\x1b[?1049l");
        Assert.False(s.UsingAlternateScreen);
        Assert.Equal("MAIN    ", Text(s, 0));
        Assert.Equal(4, s.CursorX);   // 光标随 1049 一起恢复
    }

    // ---------------------------------------------------------------- 尺寸变化

    [Fact]
    public void Resize_KeepsTopLeftContentAndClampsCursor()
    {
        var s = new TerminalScreen(10, 3);
        s.Feed("abcdefgh");

        s.Resize(5, 2);

        Assert.Equal(5, s.Columns);
        Assert.Equal(2, s.Rows);
        Assert.Equal("abcde", Text(s, 0));
        Assert.InRange(s.CursorX, 0, 4);
    }

    // ---------------------------------------------------------------- OSC

    [Fact]
    public void Osc0And2_SetTitle_BothTerminators()
    {
        var s = new TerminalScreen(10, 2);
        var titles = new List<string>();
        s.TitleChanged += titles.Add;

        s.Feed("\x1b]0;hello\x07");          // BEL 结尾
        s.Feed("\x1b]2;world\x1b\\");        // ST 结尾

        Assert.Equal(new[] { "hello", "world" }, titles);
    }

    [Fact]
    public void Osc8_AttachesHyperlinkToPrintedCellsOnly()
    {
        var s = new TerminalScreen(20, 2);

        s.Feed("\x1b]8;;https://example.com\x1b\\link\x1b]8;;\x1b\\");

        Assert.Equal("https://example.com", s.LinkAt(0, 0, 0));
        Assert.Equal("https://example.com", s.LinkAt(0, 3, 0));
        Assert.Null(s.LinkAt(0, 4, 0));      // 链接结束后的格子不属于任何链接
    }

    [Fact]
    public void Osc52_DecodesBase64ClipboardRequest()
    {
        var s = new TerminalScreen(10, 2);
        var texts = new List<string>();
        s.ClipboardRequested += texts.Add;

        s.Feed("\x1b]52;c;aGk=\x07");   // "aGk=" = "hi"

        Assert.Equal("hi", Assert.Single(texts));
    }

    [Fact]
    public void Osc52_InvalidBase64_IsIgnoredWithoutThrowing()
    {
        var s = new TerminalScreen(10, 2);
        var texts = new List<string>();
        s.ClipboardRequested += texts.Add;

        s.Feed("\x1b]52;c;!!!not-base64!!!\x07");

        Assert.Empty(texts);
    }

    [Fact]
    public void Osc10Query_RespondsWithXtermColor()
    {
        var s = new TerminalScreen(10, 2);
        var replies = new List<string>();
        s.ResponseReady += replies.Add;

        s.Feed("\x1b]10;?\x07");

        var reply = Assert.Single(replies);
        Assert.StartsWith("\x1b]10;rgb:", reply);
        Assert.EndsWith("\x1b\\", reply);
    }

    // ---------------------------------------------------------------- 应答序列

    [Fact]
    public void Da1_Query_RespondsWithVt100Identity()
    {
        var s = new TerminalScreen(10, 2);
        var replies = new List<string>();
        s.ResponseReady += replies.Add;

        s.Feed("\x1b[c");

        Assert.Equal("\x1b[?1;2c", Assert.Single(replies));
    }

    [Fact]
    public void Dsr6_ReportsOneBasedCursorPosition()
    {
        var s = new TerminalScreen(10, 3);
        var replies = new List<string>();
        s.ResponseReady += replies.Add;
        s.Feed("\x1b[2;3H");

        s.Feed("\x1b[6n");

        Assert.Equal("\x1b[2;3R", Assert.Single(replies));
    }

    [Fact]
    public void DecRpm_ReportsModeState()
    {
        var s = new TerminalScreen(10, 2);
        var replies = new List<string>();
        s.ResponseReady += replies.Add;

        s.Feed("\x1b[?1006$p");    // 1006 未设置 → 2（已复位）

        Assert.Equal("\x1b[?1006;2$y", Assert.Single(replies));
    }

    [Fact]
    public void XtVersion_Query_IdentifiesAsDshDesktop()
    {
        var s = new TerminalScreen(10, 2);
        var replies = new List<string>();
        s.ResponseReady += replies.Add;

        s.Feed("\x1b[>0q");

        var reply = Assert.Single(replies);
        Assert.StartsWith("\x1bP>|DSHDesktop ", reply);
        Assert.EndsWith("\x1b\\", reply);
    }
}
