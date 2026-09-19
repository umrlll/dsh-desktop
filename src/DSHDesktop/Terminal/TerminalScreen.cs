using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace DSHDesktop.Terminal;

/// <summary>终端单元格：一个字符加前景/背景色与显示属性。</summary>
public struct TerminalCell
{
    public const byte Bold = 1;
    public const byte Dim = 2;
    public const byte Underline = 4;
    public const byte Reverse = 8;
    public const byte WideTrail = 16;
    public const byte Italic = 32;
    public const byte Strike = 64;
    public const byte Blink = 128;
    public const byte AttrMask = Bold | Dim | Underline | Reverse | Italic | Strike | Blink;

    /// <summary>显示的字符；宽字符的右半格为 '\0'。</summary>
    public char Char;

    /// <summary>
    /// 紧随 <see cref="Char"/> 的第二个 UTF-16 码元：组合附加符（如 é 的重音），
    /// 或代理对的低半（emoji 等补充平面字符）。为 '\0' 时无附加。
    /// </summary>
    public char Comb;

    public uint Fg;
    public uint Bg;
    public byte Flags;

    /// <summary>OSC 8 超链接 id（1 起）；0 表示该格不属于任何链接。</summary>
    public ushort Link;

    public TerminalCell(char ch, uint fg, uint bg, byte flags)
    {
        Char = ch;
        Comb = '\0';
        Fg = fg;
        Bg = bg;
        Flags = flags;
        Link = 0;
    }
}

/// <summary>
/// VT100/xterm 屏幕缓冲：解析 ConPTY 输出中的控制序列，维护光标、滚动区域、
/// 备用屏幕、回滚缓冲、SGR 颜色与终端模式，并在应用查询时生成应答序列。
/// 所有公开访问都应在 <see cref="SyncRoot"/> 上同步（渲染线程与读取线程并发访问）。
///
/// 应答覆盖 dsh-TUI 一类 TUI 赖以完成能力探测的查询：DA1/DA2、DSR(5/6)、
/// DECRQM/DECRPM、XTVERSION、OSC 10/11/12 颜色查询，以及鼠标/焦点/括号粘贴/
/// win32 输入模式（DECSET 9001）等模式位——缺少它们时应用只能按超时降级。
/// </summary>
public sealed class TerminalScreen
{
    public const int MaxScrollback = 10000;
    public const uint DefaultFg = 0xFFD4D9E1;
    public const uint DefaultBg = 0xFF0E1114;
    public const uint DefaultCursor = 0xFFD4D9E1;

    /// <summary>XTVERSION 应答中的终端名；不要伪装成 xterm.js/Windows Terminal。</summary>
    public static readonly string TerminalVersion =
        typeof(TerminalScreen).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    private readonly List<TerminalCell[]> _scrollback = new();
    private TerminalCell[][] _lines = Array.Empty<TerminalCell[]>();
    private TerminalCell[][]? _altLines;
    private bool _usingAlt;

    private int _cx, _cy, _savedX, _savedY;
    private int _lastX, _lastY;
    private int _top, _bottom;
    private bool _wrapPending;
    private bool _autoWrap = true;
    private bool _originMode;
    private bool _insertMode;
    private bool _cursorVisible = true;
    private bool _cursorBlink = true;
    private int _cursorStyle;
    private uint _fg = DefaultFg;
    private uint _bg = DefaultBg;
    private byte _flags;
    private char _lastChar = ' ';
    private char _pendingHigh;

    // 字符集（G0/G1）与 DEC 特殊图形
    private char _g0 = 'B', _g1 = 'B';
    private int _gl;
    private char _charsetDesignator;

    // 制表位
    private bool[] _tabs = Array.Empty<bool>();

    // 模式位（供渲染/输入层读取）
    private bool _appCursorKeys;
    private bool _bracketedPaste;
    private bool _focusEvents;
    private bool _syncOutput;
    private bool _win32InputMode;
    private int _mouseMode;          // 0 / 1000 / 1002 / 1003
    private bool _mouseSgr;          // 1006
    private bool _mouseUtf8;         // 1005
    private bool _mouseUrxvt;        // 1015
    private bool _mouseSgrPixels;    // 1016

    // OSC 8 超链接表
    private readonly List<string> _links = new();
    private ushort _currentLink;

    private readonly StringBuilder _resp = new();

    private enum PState { Ground, Esc, EscInter, Csi, CsiInter, CsiIgnore, Osc, OscEsc, Dcs, DcsEsc }
    private PState _state = PState.Ground;
    private readonly List<int> _params = new(8);
    private int _param = -1;
    private char _privateChar;
    private char _intermediate;
    private readonly StringBuilder _osc = new();

    public object SyncRoot { get; } = new();
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int Version { get; private set; }
    public int ScrollbackCount => _scrollback.Count;
    public int CursorX => _cx;
    public int CursorY => _cy;
    public bool CursorVisible => _cursorVisible;

    // ---------------------------------------------------------------- 模式/状态查询

    public bool AutoWrap => _autoWrap;
    public bool OriginMode => _originMode;
    public bool InsertMode => _insertMode;
    public bool AppCursorKeys => _appCursorKeys;
    public bool BracketedPaste => _bracketedPaste;
    public bool FocusEvents => _focusEvents;
    public bool Win32InputMode => _win32InputMode;
    public bool SynchronizedOutput => _syncOutput;
    public int MouseMode => _mouseMode;
    public bool MouseSgr => _mouseSgr;
    public bool MouseUtf8 => _mouseUtf8;
    public bool MouseUrxvt => _mouseUrxvt;
    public bool MouseSgrPixels => _mouseSgrPixels;
    public bool UsingAlternateScreen => _usingAlt;
    public int CursorStyle => _cursorStyle;
    public bool CursorBlink => _cursorBlink;

    /// <summary>需要写回终端进程的应答字节（DA1、DECRPM、OSC 应答等）。在读取线程上触发。</summary>
    public event Action<string>? ResponseReady;

    /// <summary>OSC 0/1/2 设置的窗口标题。</summary>
    public event Action<string>? TitleChanged;

    /// <summary>OSC 52 写入剪贴板的请求（已解码的文本）。</summary>
    public event Action<string>? ClipboardRequested;

    public TerminalScreen(int columns, int rows)
    {
        Columns = Math.Max(2, columns);
        Rows = Math.Max(2, rows);
        ResetTabs();
        AllocateLines();
    }

    // ---------------------------------------------------------------- 屏幕尺寸

    private void ResetTabs()
    {
        _tabs = new bool[Columns];
        for (var i = 8; i < Columns; i += 8) _tabs[i] = true;
    }

    private void AllocateLines()
    {
        _lines = NewBuffer();
        _top = 0;
        _bottom = Rows - 1;
        _cx = Math.Min(_cx, Columns - 1);
        _cy = Math.Min(_cy, Rows - 1);
        Version++;
    }

    private TerminalCell[][] NewBuffer()
    {
        var buffer = new TerminalCell[Rows][];
        for (var i = 0; i < Rows; i++) buffer[i] = NewBlankLine();
        return buffer;
    }

    private TerminalCell[] NewBlankLine()
    {
        var line = new TerminalCell[Columns];
        var blank = new TerminalCell(' ', _fg, _bg, 0);
        for (var i = 0; i < line.Length; i++) line[i] = blank;
        return line;
    }

    /// <summary>调整行列数；保留内容，重置滚动区域与制表位。</summary>
    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(2, rows);
        if (columns == Columns && rows == Rows) return;

        _lines = ReflowBuffer(_lines, columns, rows);
        if (_altLines != null) _altLines = ReflowBuffer(_altLines, columns, rows);

        Columns = columns;
        Rows = rows;
        ResetTabs();
        _top = 0;
        _bottom = Rows - 1;
        _cx = Math.Min(_cx, Columns - 1);
        _cy = Math.Min(_cy, Rows - 1);
        _wrapPending = false;
        Version++;
    }

    private static TerminalCell[][] ReflowBuffer(TerminalCell[][] old, int columns, int rows)
    {
        var oldRows = old.Length;
        var oldCols = oldRows > 0 ? old[0].Length : 0;
        var fresh = new TerminalCell[rows][];
        for (var r = 0; r < rows; r++)
        {
            var line = new TerminalCell[columns];
            var blank = new TerminalCell(' ', DefaultFg, DefaultBg, 0);
            for (var i = 0; i < columns; i++) line[i] = blank;
            if (r < oldRows) Array.Copy(old[r], line, Math.Min(oldCols, columns));
            // 截断处若切断了宽字符，清掉悬空的右半格
            for (var i = 0; i < columns; i++)
            {
                if ((line[i].Flags & TerminalCell.WideTrail) == 0) continue;
                if (i == 0 || (line[i - 1].Flags & TerminalCell.WideTrail) != 0 || line[i - 1].Char == '\0')
                    line[i] = new TerminalCell(' ', line[i].Fg, line[i].Bg, 0);
            }
            fresh[r] = line;
        }
        return fresh;
    }

    /// <summary>取视口某一行（<paramref name="scrollOffset"/> 为向上回滚的行数）。</summary>
    public TerminalCell[]? GetVisibleLine(int viewportRow, int scrollOffset)
    {
        var first = _scrollback.Count - scrollOffset;
        var index = first + viewportRow;
        if (index < 0) return null;
        if (index < _scrollback.Count) return _scrollback[index];
        var screenRow = index - _scrollback.Count;
        return screenRow >= 0 && screenRow < Rows ? _lines[screenRow] : null;
    }

    /// <summary>光标所在的行在视口中的位置；不在视口内时返回 -1。</summary>
    public int CursorViewportRow(int scrollOffset)
    {
        var row = _cy + scrollOffset;
        return row >= 0 && row < Rows ? row : -1;
    }

    /// <summary>取某视口格上的 OSC 8 链接（无链接返回 null）。</summary>
    public string? LinkAt(int viewportRow, int column, int scrollOffset)
    {
        var line = GetVisibleLine(viewportRow, scrollOffset);
        if (line == null || column < 0 || column >= line.Length) return null;
        var id = line[column].Link;
        if (id == 0 || id > _links.Count) return null;
        return _links[id - 1];
    }

    /// <summary>清空屏幕与回滚缓冲，光标回到左上角。</summary>
    public void Clear()
    {
        _scrollback.Clear();
        ClearScreenOnly();
        _cx = 0;
        _cy = 0;
        _wrapPending = false;
        Version++;
    }

    private void ClearScreenOnly()
    {
        for (var r = 0; r < Rows; r++) ClearLine(r);
    }

    // ---------------------------------------------------------------- 输入

    /// <summary>喂入一段已解码的文本。</summary>
    public void Feed(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        string? reply = null;
        lock (SyncRoot)
        {
            _resp.Clear();
            foreach (var ch in text) Process(ch);
            Version++;
            if (_resp.Length > 0) reply = _resp.ToString();
        }
        // 在锁外写回，避免响应通道阻塞读取线程时把渲染也一起卡住
        if (reply != null) ResponseReady?.Invoke(reply);
    }

    private void Process(char c)
    {
        switch (_state)
        {
            case PState.Ground:
                if (c == '\x1b') { ResetCsi(); _state = PState.Esc; }
                else if (c < ' ') Control(c);
                else if (c != '\x7f') PutChar(c);
                break;

            case PState.Esc:
                switch (c)
                {
                    case '[': ResetCsi(); _state = PState.Csi; break;
                    case ']': _osc.Clear(); _state = PState.Osc; break;
                    case 'P': _state = PState.Dcs; break;
                    case '(': case ')': case '*': case '+':
                        _charsetDesignator = c; _state = PState.EscInter; break;
                    case '7': _savedX = _cx; _savedY = _cy; _state = PState.Ground; break;
                    case '8': RestoreCursor(); _state = PState.Ground; break;
                    case 'D': LineFeed(); _state = PState.Ground; break;
                    case 'E': _cx = 0; LineFeed(); _state = PState.Ground; break;
                    case 'M': ReverseIndex(); _state = PState.Ground; break;
                    case 'c': HardReset(); _state = PState.Ground; break;
                    case '=': case '>': break;   // DECKPAM/DECKPNM：键区模式，键盘层不区分
                    default: _state = PState.Ground; break;
                }
                break;

            case PState.EscInter:
                DesignateCharset(c);
                _charsetDesignator = '\0';
                _state = PState.Ground;
                break;

            case PState.Csi:
                if (c >= '0' && c <= '9') _param = (_param < 0 ? 0 : _param) * 10 + (c - '0');
                else if (c == ';') { _params.Add(_param); _param = -1; }
                else if (c == '?' || c == '>' || c == '<' || c == '=')
                {
                    if (_params.Count == 0 && _param < 0) _privateChar = c;
                }
                else if (c >= 0x40 && c <= 0x7E)
                {
                    _params.Add(_param);
                    DispatchCsi(c);
                    ResetCsi();
                    _state = PState.Ground;
                }
                else if (c >= 0x20 && c <= 0x2F) { _intermediate = c; _state = PState.CsiInter; }
                else if (c == ':') { /* 子参数分隔符：按参数边界处理 */ _params.Add(_param); _param = -1; }
                else if (c < 0x20) Control(c);
                else _state = PState.CsiIgnore;
                break;

            case PState.CsiInter:
                if (c >= 0x40 && c <= 0x7E)
                {
                    _params.Add(_param);
                    DispatchCsi(c);
                    ResetCsi();
                    _state = PState.Ground;
                }
                else if (c < 0x20) Control(c);
                else if (c > 0x2F) _state = PState.CsiIgnore;
                break;

            case PState.CsiIgnore:
                if (c >= 0x40 && c <= 0x7E) { ResetCsi(); _state = PState.Ground; }
                break;

            case PState.Osc:
                if (c == '\a') { HandleOsc(); _state = PState.Ground; }
                else if (c == '\x1b') _state = PState.OscEsc;
                else if (_osc.Length < 8192) _osc.Append(c);
                break;

            case PState.OscEsc:
                if (c == '\\') { HandleOsc(); _state = PState.Ground; }
                else { _state = PState.Osc; }
                break;

            case PState.Dcs:
                // DCS 内容（如终端自身发出的查询应答）本终端不需要，整体丢弃
                if (c == '\x1b') _state = PState.DcsEsc;
                break;

            case PState.DcsEsc:
                _state = c == '\\' ? PState.Ground : PState.Dcs;
                break;
        }
    }

    private void ResetCsi()
    {
        _params.Clear();
        _param = -1;
        _privateChar = '\0';
        _intermediate = '\0';
    }

    private void Control(char c)
    {
        switch (c)
        {
            case '\b':
                if (_wrapPending) _wrapPending = false;
                else if (_cx > 0) _cx--;
                break;
            case '\t': ForwardTab(1); break;
            case '\n': case '\v': case '\f': LineFeed(); break;
            case '\r': _cx = 0; _wrapPending = false; break;
            case '\x0e': _gl = 1; break;   // SO：切到 G1
            case '\x0f': _gl = 0; break;   // SI：切回 G0
            case '\a': break;
        }
    }

    // ---------------------------------------------------------------- 打印

    private void PutChar(char c)
    {
        if (char.IsHighSurrogate(c)) { _pendingHigh = c; return; }
        if (char.IsLowSurrogate(c))
        {
            if (_pendingHigh == '\0') return;
            var high = _pendingHigh;
            _pendingHigh = '\0';
            PutRune(high, c, IsWideCodePoint(char.ConvertToUtf32(high, c)));
            return;
        }
        if (_pendingHigh != '\0')
        {
            var orphan = _pendingHigh;
            _pendingHigh = '\0';
            PutRune(orphan, '\0', IsWide(orphan));
        }

        var mapped = TranslateCharset(c);
        if (IsZeroWidth(mapped))
        {
            AttachCombining(mapped);
            return;
        }
        PutRune(mapped, '\0', IsWide(mapped));
    }

    private void PutRune(char ch, char comb, bool wide)
    {
        if (_wrapPending)
        {
            _cx = 0;
            LineFeed();
        }
        _wrapPending = false;
        if (_cx < 0) _cx = 0;
        if (_cx >= Columns) _cx = Columns - 1;

        if (wide && _cx >= Columns - 1)
        {
            if (!_autoWrap)
            {
                // 自动换行关闭时，宽字符放不下就丢掉，避免半个字
                return;
            }
            _cx = 0;
            LineFeed();
        }

        var line = _lines[_cy];
        if (_insertMode) InsertBlanksAt(line, _cx, wide ? 2 : 1);
        ClearWideAt(_cy, _cx);
        if (wide) ClearWideAt(_cy, Math.Min(Columns - 1, _cx + 1));

        line[_cx] = new TerminalCell(ch, _fg, _bg, _flags) { Comb = comb, Link = _currentLink };
        _lastX = _cx;
        _lastY = _cy;
        if (wide && _cx + 1 < Columns)
        {
            line[_cx + 1] = new TerminalCell('\0', _fg, _bg, (byte)(_flags | TerminalCell.WideTrail))
            {
                Link = _currentLink,
            };
        }

        _lastChar = ch;
        _cx += wide ? 2 : 1;
        if (_cx >= Columns)
        {
            _cx = Columns - 1;
            _wrapPending = _autoWrap;
        }
    }

    private static void InsertBlanksAt(TerminalCell[] line, int at, int count)
    {
        if (at < 0 || at >= line.Length || count <= 0) return;
        var move = Math.Min(count, line.Length - at);
        Array.Copy(line, at, line, at + move, line.Length - at - move);
        for (var i = at; i < at + move; i++) line[i] = new TerminalCell(' ', line[i].Fg, line[i].Bg, 0);
    }

    /// <summary>覆盖某格前先清掉可能与之相连的宽字符另一半。</summary>
    private void ClearWideAt(int row, int col)
    {
        if (row < 0 || row >= Rows) return;
        var line = _lines[row];
        if (col < 0 || col >= Columns) return;
        if ((line[col].Flags & TerminalCell.WideTrail) != 0)
        {
            if (col > 0) line[col - 1] = new TerminalCell(' ', line[col - 1].Fg, line[col - 1].Bg, 0);
            line[col] = new TerminalCell(' ', line[col].Fg, line[col].Bg, 0);
            return;
        }
        if (col + 1 < Columns && (line[col + 1].Flags & TerminalCell.WideTrail) != 0)
        {
            line[col] = new TerminalCell(' ', line[col].Fg, line[col].Bg, 0);
            line[col + 1] = new TerminalCell(' ', line[col + 1].Fg, line[col + 1].Bg, 0);
        }
    }

    private void AttachCombining(char c)
    {
        if (_lastY < 0 || _lastY >= Rows) return;
        var line = _lines[_lastY];
        var x = _lastX;
        if (x < 0 || x >= Columns) return;
        if ((line[x].Flags & TerminalCell.WideTrail) != 0 && x > 0) x--;
        if (line[x].Comb == '\0') line[x].Comb = c;
    }

    private char TranslateCharset(char c)
    {
        var set = _gl == 0 ? _g0 : _g1;
        if (set != '0') return c;
        return c switch
        {
            '`' => '◆', 'a' => '▒', 'b' => '␉', 'c' => '␌', 'd' => '␍', 'e' => '␊',
            'f' => '°', 'g' => '±', 'h' => '␤', 'i' => '␋', 'j' => '┘', 'k' => '┐',
            'l' => '┌', 'm' => '└', 'n' => '┼', 'o' => '⎺', 'p' => '⎻', 'q' => '─',
            'r' => '⎼', 's' => '⎽', 't' => '├', 'u' => '┤', 'v' => '┴', 'w' => '┬',
            'x' => '│', 'y' => '≤', 'z' => '≥', '{' => 'π', '|' => '≠', '}' => '£',
            '~' => '·',
            _ => c,
        };
    }

    private void DesignateCharset(char designator)
    {
        // ESC ( <d> → G0；ESC ) <d> → G1；其余（*、+）是 G2/G3，本终端不建模
        if (_charsetDesignator == '(') _g0 = designator;
        else if (_charsetDesignator == ')') _g1 = designator;
    }

    private static bool IsWide(char c) =>
        (c >= 0x1100 && c <= 0x115F) ||
        (c >= 0x2E80 && c <= 0x303E) ||
        (c >= 0x3041 && c <= 0x33FF) ||
        (c >= 0x3400 && c <= 0x4DBF) ||
        (c >= 0x4E00 && c <= 0x9FFF) ||
        (c >= 0xA000 && c <= 0xA4CF) ||
        (c >= 0xAC00 && c <= 0xD7A3) ||
        (c >= 0xF900 && c <= 0xFAFF) ||
        (c >= 0xFE10 && c <= 0xFE19) ||
        (c >= 0xFE30 && c <= 0xFE6F) ||
        (c >= 0xFF00 && c <= 0xFF60) ||
        (c >= 0xFFE0 && c <= 0xFFE6);

    private static bool IsWideCodePoint(int cp)
    {
        if (cp <= 0xFFFF) return IsWide((char)cp);
        return (cp >= 0x1F000 && cp <= 0x1FAFF) ||
               (cp >= 0x20000 && cp <= 0x3FFFD);
    }

    private static bool IsZeroWidth(char c)
    {
        if (c == '\u200b' || c == '\u200c' || c == '\u200d' || c == '\ufeff') return true;
        if (c >= '\ufe00' && c <= '\ufe0f') return true;   // 变体选择符
        if (c >= '\u20d0' && c <= '\u20ff') return true;
        switch (CharUnicodeInfo.GetUnicodeCategory(c))
        {
            case UnicodeCategory.NonSpacingMark:
            case UnicodeCategory.EnclosingMark:
            case UnicodeCategory.Format:
                return true;
            default:
                return false;
        }
    }

    // ---------------------------------------------------------------- 滚动

    private void LineFeed()
    {
        _wrapPending = false;
        if (_cy == _bottom) ScrollUp(1);
        else if (_cy < Rows - 1) _cy++;
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (_cy == _top) ScrollDown(1);
        else if (_cy > 0) _cy--;
    }

    private void ScrollUp(int n)
    {
        n = Math.Max(1, n);
        for (var i = 0; i < n; i++)
        {
            var removed = _lines[_top];
            for (var r = _top; r < _bottom; r++) _lines[r] = _lines[r + 1];
            _lines[_bottom] = NewBlankLine();
            if (!_usingAlt && _top == 0 && _bottom == Rows - 1)
            {
                _scrollback.Add(removed);
                if (_scrollback.Count > MaxScrollback)
                    _scrollback.RemoveRange(0, _scrollback.Count - MaxScrollback);
            }
        }
        Version++;
    }

    private void ScrollDown(int n)
    {
        n = Math.Max(1, n);
        for (var i = 0; i < n; i++)
        {
            for (var r = _bottom; r > _top; r--) _lines[r] = _lines[r - 1];
            _lines[_top] = NewBlankLine();
        }
        Version++;
    }

    // ---------------------------------------------------------------- 备用屏幕

    private void EnterAltScreen(bool clear, bool saveCursor)
    {
        if (saveCursor) { _savedX = _cx; _savedY = _cy; }
        if (_usingAlt)
        {
            _cx = 0; _cy = 0;
            if (clear) ClearScreenOnly();
            Version++;
            return;
        }
        _altLines ??= NewBuffer();
        var main = _lines;
        _lines = _altLines;
        _altLines = main;
        _usingAlt = true;
        _top = 0;
        _bottom = Rows - 1;
        _cx = 0;
        _cy = 0;
        _wrapPending = false;
        if (clear) ClearScreenOnly();
        Version++;
    }

    private void ExitAltScreen(bool clear, bool restoreCursor)
    {
        if (!_usingAlt)
        {
            if (clear) ClearScreenOnly();
            return;
        }
        if (clear) ClearScreenOnly();
        var alt = _lines;
        _lines = _altLines ?? NewBuffer();
        _altLines = alt;
        _usingAlt = false;
        _top = 0;
        _bottom = Rows - 1;
        if (restoreCursor) RestoreCursor();
        else { _cx = Math.Min(_cx, Columns - 1); _cy = Math.Min(_cy, Rows - 1); }
        _wrapPending = false;
        Version++;
    }

    private void RestoreCursor()
    {
        _cx = Math.Clamp(_savedX, 0, Columns - 1);
        _cy = Math.Clamp(_savedY, 0, Rows - 1);
        _wrapPending = false;
    }

    // ---------------------------------------------------------------- 应答

    private void Respond(string sequence) => _resp.Append(sequence);

    private void ReportCursor(bool decxcpr)
    {
        var row = Math.Clamp(_cy + 1, 1, Rows);
        var col = Math.Clamp(_cx + 1, 1, Columns);
        Respond(decxcpr ? $"\x1b[?{row};{col}R" : $"\x1b[{row};{col}R");
    }

    /// <summary>DEC/ANSI 模式状态：1 已设置、2 已复位、0 未识别（对应 DECRPM）。</summary>
    private int ModeStatus(int mode, bool priv)
    {
        if (!priv)
        {
            return mode switch
            {
                4 => _insertMode ? 1 : 2,
                _ => 0,
            };
        }
        return mode switch
        {
            1 => _appCursorKeys ? 1 : 2,
            6 => _originMode ? 1 : 2,
            7 => _autoWrap ? 1 : 2,
            12 => _cursorBlink ? 1 : 2,
            25 => _cursorVisible ? 1 : 2,
            47 => _usingAlt ? 1 : 2,
            1047 => _usingAlt ? 1 : 2,
            1048 => 2,
            1049 => _usingAlt ? 1 : 2,
            1000 => _mouseMode == 1000 ? 1 : 2,
            1002 => _mouseMode == 1002 ? 1 : 2,
            1003 => _mouseMode == 1003 ? 1 : 2,
            1004 => _focusEvents ? 1 : 2,
            1005 => _mouseUtf8 ? 1 : 2,
            1006 => _mouseSgr ? 1 : 2,
            1015 => _mouseUrxvt ? 1 : 2,
            1016 => _mouseSgrPixels ? 1 : 2,
            2004 => _bracketedPaste ? 1 : 2,
            2026 => _syncOutput ? 1 : 2,
            9001 => _win32InputMode ? 1 : 2,
            _ => 0,
        };
    }

    private static string XtermColor(uint argb)
    {
        var r = (argb >> 16) & 0xFF;
        var g = (argb >> 8) & 0xFF;
        var b = argb & 0xFF;
        return $"rgb:{r:x2}{r:x2}/{g:x2}{g:x2}/{b:x2}{b:x2}";
    }

    private void HandleOsc()
    {
        var body = _osc.ToString();
        if (body.Length == 0) return;
        var semi = body.IndexOf(';');
        var code = semi < 0 ? body : body[..semi];
        var payload = semi < 0 ? string.Empty : body[(semi + 1)..];

        switch (code)
        {
            case "0":
            case "1":
            case "2":
                TitleChanged?.Invoke(payload);
                break;

            case "4":
                HandleOscPalette(payload);
                break;

            case "8":
                HandleOscHyperlink(payload);
                break;

            case "10":
            case "11":
            case "12":
                if (payload.Trim() == "?")
                {
                    var color = code switch
                    {
                        "10" => _fg,
                        "12" => DefaultCursor,
                        _ => _bg,
                    };
                    Respond($"\x1b]{code};{XtermColor(color)}\x1b\\");
                }
                break;

            case "52":
                HandleOscClipboard(payload);
                break;
        }
    }

    private void HandleOscPalette(string payload)
    {
        // OSC 4 ; index ; ? —— 询问调色板颜色
        var parts = payload.Split(';');
        if (parts.Length < 2 || parts[1].Trim() != "?") return;
        if (!int.TryParse(parts[0], out var index)) return;
        if (index < 0 || index > 255) return;
        Respond($"\x1b]4;{index};{XtermColor(Palette256(index))}\x1b\\");
    }

    private void HandleOscHyperlink(string payload)
    {
        var semi = payload.IndexOf(';');
        var uri = semi < 0 ? string.Empty : payload[(semi + 1)..];
        if (uri.Length == 0)
        {
            _currentLink = 0;
            return;
        }
        var existing = _links.IndexOf(uri);
        if (existing >= 0)
        {
            _currentLink = (ushort)(existing + 1);
            return;
        }
        if (_links.Count >= ushort.MaxValue - 1)
        {
            _links.Clear();
            _currentLink = 0;
            return;
        }
        _links.Add(uri);
        _currentLink = (ushort)_links.Count;
    }

    private void HandleOscClipboard(string payload)
    {
        // OSC 52 ; selection ; base64
        var semi = payload.IndexOf(';');
        if (semi < 0) return;
        var data = payload[(semi + 1)..];
        if (data == "?" || data.Length == 0) return;
        try
        {
            var bytes = Convert.FromBase64String(data);
            if (bytes.Length > 1 << 20) return;
            ClipboardRequested?.Invoke(Encoding.UTF8.GetString(bytes));
        }
        catch
        {
            // 非法 base64：忽略，不能让剪贴板写入失败影响解析
        }
    }

    // ---------------------------------------------------------------- CSI 分发

    private int P(int i, int def = 1) => i < _params.Count && _params[i] > 0 ? _params[i] : def;

    /// <summary>取原始参数（0 合法），缺省返回 <paramref name="def"/>。</summary>
    private int Raw(int i, int def = 0) => i < _params.Count && _params[i] >= 0 ? _params[i] : def;

    private void DispatchCsi(char f)
    {
        var priv = _privateChar;
        switch (f)
        {
            case 'A':
                _cy = ClampVertical(_cy - P(0));
                _wrapPending = false;
                break;
            case 'B':
                _cy = ClampVertical(_cy + P(0));
                _wrapPending = false;
                break;
            case 'C': _cx = Math.Min(Columns - 1, _cx + P(0)); _wrapPending = false; break;
            case 'D': _cx = Math.Max(0, _cx - P(0)); _wrapPending = false; break;
            case 'E': _cy = ClampVertical(_cy + P(0)); _cx = 0; _wrapPending = false; break;
            case 'F': _cy = ClampVertical(_cy - P(0)); _cx = 0; _wrapPending = false; break;
            case 'G': case '`': _cx = Math.Clamp(P(0) - 1, 0, Columns - 1); _wrapPending = false; break;
            case 'd': _cy = ClampVertical(P(0) - 1); _wrapPending = false; break;
            case 'e': _cy = ClampVertical(_cy + P(0)); _wrapPending = false; break;
            case 'a': _cx = Math.Min(Columns - 1, _cx + P(0)); _wrapPending = false; break;
            case 'H': case 'f':
                {
                    var row = Math.Clamp(P(0) - 1, 0, Rows - 1);
                    var col = Math.Clamp(P(1) - 1, 0, Columns - 1);
                    if (_originMode) row = Math.Clamp(_top + row, _top, _bottom);
                    _cy = row;
                    _cx = col;
                    _wrapPending = false;
                    break;
                }
            case 'I': ForwardTab(P(0)); break;
            case 'Z': BackwardTab(P(0)); break;
            case 'g': ClearTabStops(Raw(0)); break;
            case 'J': EraseInDisplay(Raw(0)); break;
            case 'K': EraseInLine(Raw(0)); break;
            case 'L': InsertLines(P(0)); break;
            case 'M': DeleteLines(P(0)); break;
            case 'P': DeleteChars(P(0)); break;
            case '@': InsertChars(P(0)); break;
            case 'X': EraseChars(P(0)); break;
            case 'S': ScrollUp(P(0)); break;
            case 'T': ScrollDown(P(0)); break;
            case 'r':
                if (priv == '\0')
                {
                    var top = P(0) - 1;
                    var bottom = P(1, Rows) - 1;
                    top = Math.Clamp(top, 0, Rows - 1);
                    bottom = Math.Clamp(bottom, 0, Rows - 1);
                    if (top < bottom)
                    {
                        _top = top;
                        _bottom = bottom;
                        _cx = 0;
                        _cy = _originMode ? _top : 0;
                    }
                    _wrapPending = false;
                }
                break;
            case 'm':
                if (priv == '>') ApplyModifyOtherKeys();
                else ApplySgr();
                break;
            case 'n': DeviceStatusReport(); break;
            case 'c':
                if (priv == '>') Respond("\x1b[>1;0;0c");            // DA2
                else if (priv == '\0') Respond("\x1b[?1;2c");        // DA1：TUI 用作查询哨兵
                break;
            case 'q':
                if (_intermediate == ' ') _cursorStyle = Math.Clamp(Raw(0), 0, 6);
                else if (priv == '>') Respond($"\x1bP>|{TerminalName} {TerminalVersion}\x1b\\");  // XTVERSION
                break;
            case 'p':
                if (_intermediate == '$')
                {
                    var mode = Raw(0);
                    var status = ModeStatus(mode, priv == '?');
                    Respond(priv == '?' ? $"\x1b[?{mode};{status}$y" : $"\x1b[{mode};{status}$y");
                }
                else if (_intermediate == '!') SoftReset();               // DECSTR
                break;
            case 'u':
                if (priv == '\0' && _params.Count <= 1 && Raw(0) == 0 && _intermediate == '\0')
                    RestoreCursor();
                break;
            case 's':
                if (priv == '\0' && _intermediate == '\0' && _params.Count <= 1 && Raw(0) == 0)
                {
                    _savedX = _cx;
                    _savedY = _cy;
                }
                break;
            case 'h': SetMode(true, priv); break;
            case 'l': SetMode(false, priv); break;
            case 'b':
                if (_lastChar != ' ')
                {
                    var n = P(0);
                    for (var i = 0; i < n; i++) PutChar(_lastChar);
                }
                break;
            default:
                break;
        }
    }

    private int ClampVertical(int row)
    {
        if (_originMode || (row >= _top && row <= _bottom))
            return Math.Clamp(row, _top, _bottom);
        return Math.Clamp(row, 0, Rows - 1);
    }

    private void DeviceStatusReport()
    {
        switch (Raw(0))
        {
            case 5: Respond("\x1b[0n"); break;
            case 6: ReportCursor(_privateChar == '?'); break;
        }
    }

    private void SetMode(bool on, char priv)
    {
        if (priv == '?')
        {
            foreach (var p in _params)
            {
                switch (p)
                {
                    case 1: _appCursorKeys = on; break;
                    case 6:
                        _originMode = on;
                        _cx = 0;
                        _cy = on ? _top : 0;
                        _wrapPending = false;
                        break;
                    case 7: _autoWrap = on; break;
                    case 12: _cursorBlink = on; break;
                    case 25: _cursorVisible = on; break;
                    case 47:
                        if (on) EnterAltScreen(false, false);
                        else ExitAltScreen(false, false);
                        break;
                    case 1047:
                        if (on) EnterAltScreen(true, false);
                        else ExitAltScreen(true, false);
                        break;
                    case 1048:
                        if (on) { _savedX = _cx; _savedY = _cy; }
                        else RestoreCursor();
                        break;
                    case 1049:
                        if (on) EnterAltScreen(true, true);
                        else ExitAltScreen(false, true);
                        break;
                    case 1000: case 1002: case 1003:
                        _mouseMode = on ? p : (_mouseMode == p ? 0 : _mouseMode);
                        break;
                    case 1004: _focusEvents = on; break;
                    case 1005: _mouseUtf8 = on; break;
                    case 1006: _mouseSgr = on; break;
                    case 1015: _mouseUrxvt = on; break;
                    case 1016: _mouseSgrPixels = on; break;
                    case 2004: _bracketedPaste = on; break;
                    case 2026: _syncOutput = on; break;
                    case 9001: _win32InputMode = on; break;
                    default: break;
                }
            }
        }
        else if (priv == '\0')
        {
            foreach (var p in _params)
            {
                if (p == 4) _insertMode = on;
            }
        }
        Version++;
    }

    private void ApplyModifyOtherKeys()
    {
        // CSI > 4 m / CSI > 4 ; 2 m —— xterm modifyOtherKeys。
        // 本终端用 win32 输入模式表达修饰键（Windows 上更完整），这里只记录状态、
        // 不产生副作用，避免与 9001 冲突。
    }

    private void SoftReset()
    {
        _fg = DefaultFg;
        _bg = DefaultBg;
        _flags = 0;
        _cursorVisible = true;
        _cursorBlink = true;
        _originMode = false;
        _insertMode = false;
        _autoWrap = true;
        _top = 0;
        _bottom = Rows - 1;
        _g0 = 'B';
        _g1 = 'B';
        _gl = 0;
        _wrapPending = false;
        Version++;
    }

    private void InsertLines(int n)
    {
        if (_cy < _top || _cy > _bottom) return;
        n = Math.Clamp(n, 1, _bottom - _cy + 1);
        for (var i = 0; i < n; i++)
        {
            for (var r = _bottom; r > _cy; r--) _lines[r] = _lines[r - 1];
            _lines[_cy] = NewBlankLine();
        }
        _cx = 0;
        Version++;
    }

    private void DeleteLines(int n)
    {
        if (_cy < _top || _cy > _bottom) return;
        n = Math.Clamp(n, 1, _bottom - _cy + 1);
        for (var i = 0; i < n; i++)
        {
            for (var r = _cy; r < _bottom; r++) _lines[r] = _lines[r + 1];
            _lines[_bottom] = NewBlankLine();
        }
        _cx = 0;
        Version++;
    }

    private void DeleteChars(int n)
    {
        n = Math.Clamp(n, 1, Columns - _cx);
        var line = _lines[_cy];
        ClearWideAt(_cy, _cx);
        ClearWideAt(_cy, Math.Min(Columns - 1, _cx + n));
        Array.Copy(line, _cx + n, line, _cx, Columns - _cx - n);
        for (var i = Columns - n; i < Columns; i++) line[i] = new TerminalCell(' ', _fg, _bg, 0);
        Version++;
    }

    private void InsertChars(int n)
    {
        n = Math.Clamp(n, 1, Columns - _cx);
        var line = _lines[_cy];
        ClearWideAt(_cy, _cx);
        if (Columns - _cx - n > 0) Array.Copy(line, _cx, line, _cx + n, Columns - _cx - n);
        for (var i = _cx; i < _cx + n; i++) line[i] = new TerminalCell(' ', _fg, _bg, 0);
        Version++;
    }

    private void EraseChars(int n)
    {
        var line = _lines[_cy];
        var end = Math.Min(Columns, _cx + Math.Max(1, n));
        ClearWideAt(_cy, _cx);
        ClearWideAt(_cy, end - 1);
        for (var i = _cx; i < end; i++) line[i] = new TerminalCell(' ', _fg, _bg, 0);
        Version++;
    }

    private void EraseInLine(int mode)
    {
        var line = _lines[_cy];
        int from, to;
        switch (mode)
        {
            case 1: from = 0; to = _cx; break;
            case 2: from = 0; to = Columns - 1; break;
            default: from = _cx; to = Columns - 1; break;
        }
        if (from <= to)
        {
            from = Math.Max(0, from);
            ClearWideAt(_cy, from);
            ClearWideAt(_cy, Math.Min(Columns - 1, to));
        }
        for (var i = from; i <= to && i < Columns; i++) line[i] = new TerminalCell(' ', _fg, _bg, 0);
        Version++;
    }

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 3:
                _scrollback.Clear();
                break;
            case 1:
                for (var r = 0; r < _cy; r++) ClearLine(r);
                EraseInLine(1);
                break;
            case 2:
                for (var r = 0; r < Rows; r++) ClearLine(r);
                break;
            default:
                EraseInLine(0);
                for (var r = _cy + 1; r < Rows; r++) ClearLine(r);
                break;
        }
        Version++;
    }

    private void ClearLine(int row)
    {
        var line = _lines[row];
        for (var i = 0; i < Columns; i++) line[i] = new TerminalCell(' ', _fg, _bg, 0);
    }

    private void ForwardTab(int n)
    {
        _wrapPending = false;
        for (var i = 0; i < Math.Max(1, n); i++)
        {
            var next = Columns - 1;
            for (var x = _cx + 1; x < Columns; x++)
            {
                if (_tabs[x]) { next = x; break; }
            }
            _cx = next;
            if (_cx >= Columns - 1) break;
        }
    }

    private void BackwardTab(int n)
    {
        _wrapPending = false;
        for (var i = 0; i < Math.Max(1, n); i++)
        {
            var prev = 0;
            for (var x = _cx - 1; x >= 0; x--)
            {
                if (_tabs[x]) { prev = x; break; }
            }
            _cx = prev;
            if (_cx == 0) break;
        }
    }

    private void ClearTabStops(int mode)
    {
        if (mode == 3)
        {
            Array.Clear(_tabs, 0, _tabs.Length);
            return;
        }
        if (mode == 0 && _cx >= 0 && _cx < _tabs.Length) _tabs[_cx] = false;
    }

    private void HardReset()
    {
        if (_usingAlt && _altLines != null)
        {
            _lines = _altLines;
            _usingAlt = false;
        }
        _scrollback.Clear();
        _links.Clear();
        _currentLink = 0;
        _altLines = null;
        _fg = DefaultFg;
        _bg = DefaultBg;
        _flags = 0;
        _cx = 0;
        _cy = 0;
        _savedX = 0;
        _savedY = 0;
        _top = 0;
        _bottom = Rows - 1;
        _autoWrap = true;
        _originMode = false;
        _insertMode = false;
        _cursorVisible = true;
        _cursorBlink = true;
        _cursorStyle = 0;
        _appCursorKeys = false;
        _bracketedPaste = false;
        _focusEvents = false;
        _syncOutput = false;
        _win32InputMode = false;
        _mouseMode = 0;
        _mouseSgr = false;
        _mouseUtf8 = false;
        _mouseUrxvt = false;
        _mouseSgrPixels = false;
        _g0 = 'B';
        _g1 = 'B';
        _gl = 0;
        _pendingHigh = '\0';
        ResetTabs();
        for (var r = 0; r < Rows; r++) ClearLine(r);
        Version++;
    }

    // ---------------------------------------------------------------- SGR

    private void ApplySgr()
    {
        // 解析器总会把最后一个参数压栈（无参数时为 -1），所以 ESC[m 得到的是 [-1]。
        // 空参数必须按 0 处理——ESC[m 就是复位，PowerShell/PSReadLine 大量使用它，
        // 若当成未知参数忽略，颜色就会一直延续到命令输出上。
        if (_params.Count == 0) { ResetSgr(); return; }
        for (var i = 0; i < _params.Count; i++)
        {
            var p = _params[i] < 0 ? 0 : _params[i];
            switch (p)
            {
                case 0: ResetSgr(); break;
                case 1: _flags |= TerminalCell.Bold; break;
                case 2: _flags |= TerminalCell.Dim; break;
                case 3: _flags |= TerminalCell.Italic; break;
                case 4: _flags |= TerminalCell.Underline; break;
                case 5: case 6: _flags |= TerminalCell.Blink; break;
                case 7: _flags |= TerminalCell.Reverse; break;
                case 9: _flags |= TerminalCell.Strike; break;
                case 21: _flags |= TerminalCell.Underline; break;
                case 22: _flags &= unchecked((byte)~(TerminalCell.Bold | TerminalCell.Dim)); break;
                case 23: _flags &= unchecked((byte)~TerminalCell.Italic); break;
                case 24: _flags &= unchecked((byte)~TerminalCell.Underline); break;
                case 25: _flags &= unchecked((byte)~TerminalCell.Blink); break;
                case 27: _flags &= unchecked((byte)~TerminalCell.Reverse); break;
                case 29: _flags &= unchecked((byte)~TerminalCell.Strike); break;
                case 39: _fg = DefaultFg; break;
                case 49: _bg = DefaultBg; break;
                default:
                    if (p >= 30 && p <= 37) _fg = Palette[p - 30];
                    else if (p >= 40 && p <= 47) _bg = Palette[p - 40];
                    else if (p >= 90 && p <= 97) _fg = Palette[8 + p - 90];
                    else if (p >= 100 && p <= 107) _bg = Palette[8 + p - 100];
                    else if (p == 38 || p == 48)
                    {
                        var color = ReadExtendedColor(ref i);
                        if (p == 38) _fg = color; else _bg = color;
                    }
                    break;
            }
        }
        Version++;
    }

    private uint ReadExtendedColor(ref int i)
    {
        if (i + 1 >= _params.Count) return DefaultFg;
        var mode = _params[i + 1];
        if (mode == 5 && i + 2 < _params.Count)
        {
            var idx = Math.Clamp(_params[i + 2], 0, 255);
            i += 2;
            return Palette256(idx);
        }
        if (mode == 2 && i + 4 < _params.Count)
        {
            var r = Math.Clamp(_params[i + 2], 0, 255);
            var g = Math.Clamp(_params[i + 3], 0, 255);
            var b = Math.Clamp(_params[i + 4], 0, 255);
            i += 4;
            return 0xFF000000u | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
        }
        return DefaultFg;
    }

    private void ResetSgr()
    {
        _fg = DefaultFg;
        _bg = DefaultBg;
        _flags = 0;
    }

    /// <summary>该实现自称的终端名（XTVERSION 应答）。</summary>
    public const string TerminalName = "DSHDesktop";

    private static readonly uint[] Palette =
    {
        0xFF000000, 0xFFCD3131, 0xFF0DBC79, 0xFFE5E510, 0xFF2472C8, 0xFFBC3FBC, 0xFF11A8CD, 0xFFE5E5E5,
        0xFF666666, 0xFFF14C4C, 0xFF23D18B, 0xFFF5F543, 0xFF3B8EEA, 0xFFD670D6, 0xFF29B8DB, 0xFFFFFFFF,
    };

    private static uint Palette256(int index)
    {
        if (index < 0) index = 0;
        if (index < 16) return Palette[index];
        if (index < 232)
        {
            var i = index - 16;
            var r = i / 36;
            var g = (i / 6) % 6;
            var b = i % 6;
            return 0xFF000000u
                | ((uint)(r == 0 ? 0 : 55 + r * 40) << 16)
                | ((uint)(g == 0 ? 0 : 55 + g * 40) << 8)
                | (uint)(b == 0 ? 0 : 55 + b * 40);
        }
        var v = 8 + (index - 232) * 10;
        return 0xFF000000u | ((uint)v << 16) | ((uint)v << 8) | (uint)v;
    }
}
