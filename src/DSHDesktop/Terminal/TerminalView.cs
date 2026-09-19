using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace DSHDesktop.Terminal;

/// <summary>
/// 集成终端控件：把 <see cref="TerminalScreen"/> 的字符缓冲绘制成等宽文本，
/// 并把键盘/鼠标/焦点/粘贴事件按应用启用的终端模式转发给 ConPTY 会话。
///
/// 输入侧遵守 TUI 应用（如 dsh-TUI）实际会打开的模式：
/// win32 输入模式（DECSET 9001，Windows 上唯一能保留 Enter 修饰位的编码）、
/// 应用光标键（DECCKM）、括号粘贴（2004）、鼠标上报（1000/1002/1003 + SGR 1006）、
/// 焦点事件（1004）。未启用时退回经典 VT 编码。
/// </summary>
public sealed class TerminalView : FrameworkElement
{
    private const double PadX = 8;
    private const double PadY = 6;
    /// <summary>同步输出（DECSET 2026）的兜底刷新上限：应用开了却不关时最多推迟这么久。</summary>
    private const double SyncOutputFlushMs = 200;

    private readonly Typeface _typeface;
    private readonly Typeface _boldTypeface;
    private readonly Typeface _italicTypeface;
    private readonly Dictionary<uint, SolidColorBrush> _brushCache = new();
    private readonly double _fontSize = 13.0;
    private readonly DispatcherTimer _blinkTimer;
    private readonly DispatcherTimer _syncFlushTimer;

    private TerminalScreen? _screen;
    private ConPtySession? _session;
    private double _cellW = 8;
    private double _cellH = 17;
    private int _cols = 80;
    private int _rows = 24;
    private int _scrollOffset;
    private int _invalidatePending;
    private bool _cursorOn = true;
    private string? _hoverLink;

    /// <summary>同步输出组帧期间被推迟的重绘（读取线程写、UI 线程读）。</summary>
    private volatile bool _syncRedrawDeferred;

    public TerminalView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.IBeam;
        ClipToBounds = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(this, TextRenderingMode.ClearType);

        var family = new FontFamily("Cascadia Mono, Consolas, Courier New");
        _typeface = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        _boldTypeface = new Typeface(family, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        _italicTypeface = new Typeface(family, FontStyles.Italic, FontWeights.Normal, FontStretches.Normal);

        _blinkTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(530),
        };
        _blinkTimer.Tick += (_, _) =>
        {
            _cursorOn = !_cursorOn;
            if (IsSyncOutputActive()) return;   // 组帧期间连光标闪烁一起推迟，避免撕到半帧
            InvalidateVisual();
        };

        // 同步输出的兜底：应用开了 2026 却迟迟不关（崩溃/卡住）时，最多推迟 SyncOutputFlushMs 就画一帧，
        // 否则界面会停在旧帧上。正常的同步输出会在下一批输出里关闭该模式，走不到这里。
        _syncFlushTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(SyncOutputFlushMs),
        };
        _syncFlushTimer.Tick += (_, _) =>
        {
            _syncFlushTimer.Stop();
            if (!_syncRedrawDeferred) return;
            ScheduleRedraw();
        };
    }

    /// <summary>终端进程退出时触发。</summary>
    public event Action? Exited;

    /// <summary>应用通过 OSC 0/1/2 请求的窗口标题（空串表示清除）。</summary>
    public event Action<string>? TitleChanged;

    public string ShellName { get; private set; } = "终端";
    public bool IsRunning => _session is { HasExited: false };

    /// <summary>启动一个新的终端会话（已有会话会先关闭）。
    /// <paramref name="extraPath"/> 会加到子进程 PATH 前面，<paramref name="extraEnv"/> 是额外环境变量
    /// （例如把 pnpm/npm 指向可达的镜像源）；两者都只作用于这个终端子进程。</summary>
    public void Start(string? extraPath = null, IReadOnlyDictionary<string, string>? extraEnv = null)
    {
        Stop();
        var (path, name) = ConPtySession.FindShell();
        ShellName = name;

        UpdateMetrics();
        RecalcSize(force: true);
        var screen = new TerminalScreen(_cols, _rows);
        screen.TitleChanged += OnTitleChanged;
        screen.ClipboardRequested += OnClipboardRequested;
        _screen = screen;
        try
        {
            _session = new ConPtySession(screen, path, name,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), extraPath, extraEnv);
        }
        catch (Exception ex)
        {
            _screen.Feed($"\x1b[31m无法启动终端（{name}）：{ex.Message}\x1b[0m\r\n");
            _session = null;
        }
        _scrollOffset = 0;
        if (_session != null)
        {
            _session.OutputAvailable += OnOutputAvailable;
            _session.ProcessExited += OnProcessExited;
        }
        UpdateBlinkTimer();
        Dispatcher.BeginInvoke(new Action(() => { Focus(); InvalidateVisual(); }), DispatcherPriority.Input);
    }

    /// <summary>关闭终端会话（面板隐藏时保留会话，只在窗口关闭/重启时调用）。</summary>
    public void Stop()
    {
        var session = _session;
        _session = null;
        if (session != null)
        {
            session.OutputAvailable -= OnOutputAvailable;
            session.ProcessExited -= OnProcessExited;
            try { session.Dispose(); } catch { /* ignore */ }
        }
        var screen = _screen;
        if (screen != null)
        {
            screen.TitleChanged -= OnTitleChanged;
            screen.ClipboardRequested -= OnClipboardRequested;
        }
        _hoverLink = null;
        // 会话结束：同步输出的推迟状态与兜底计时器一并复位，避免把上一个会话的状态带过来
        _syncRedrawDeferred = false;
        _syncFlushTimer.Stop();
        UpdateBlinkTimer();
    }

    /// <summary>清空屏幕与回滚缓冲。</summary>
    public void ClearScreen()
    {
        var screen = _screen;
        if (screen == null) return;
        lock (screen.SyncRoot) screen.Clear();
        _scrollOffset = 0;
        InvalidateVisual();
    }

    /// <summary>把文本写进终端（调试/脚本用）。</summary>
    public void Write(string text) => _session?.Write(text);

    /// <summary>把系统剪贴板内容粘贴进终端；应用启用括号粘贴时自动加标记。</summary>
    public bool PasteFromClipboard()
    {
        var session = _session;
        if (session == null) return false;
        string text;
        try
        {
            if (!Clipboard.ContainsText()) return false;
            text = Clipboard.GetText();
        }
        catch
        {
            return false;
        }
        if (string.IsNullOrEmpty(text)) return false;

        // 终端里的换行一律按 CR 处理（粘贴多行不应触发提交；括号粘贴标记由应用解释）
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        var screen = _screen;
        var bracketed = screen != null && screen.BracketedPaste;
        session.Write(bracketed ? "\x1b[200~" + text + "\x1b[201~" : text);
        _scrollOffset = 0;
        InvalidateVisual();
        return true;
    }

    // ------------------------------------------------------------- 尺寸/度量

    private void UpdateMetrics()
    {
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        if (dpi <= 0) dpi = 1.0;
        var ft = new FormattedText("M", CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brushes.White, dpi);
        _cellW = Math.Max(4, Math.Round(ft.WidthIncludingTrailingWhitespace, 2));
        _cellH = Math.Max(8, Math.Round(ft.Height, 2));
    }

    private void RecalcSize(bool force = false)
    {
        if (ActualWidth <= 0 || ActualHeight <= 0) return;
        var cols = Math.Max(2, (int)((ActualWidth - PadX * 2) / _cellW));
        var rows = Math.Max(2, (int)((ActualHeight - PadY * 2) / _cellH));
        if (!force && cols == _cols && rows == _rows) return;
        _cols = cols;
        _rows = rows;
        var screen = _screen;
        if (screen != null)
        {
            lock (screen.SyncRoot) screen.Resize(cols, rows);
            _session?.Resize(cols, rows);
        }
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (double.IsInfinity(availableSize.Width)) availableSize.Width = 800;
        if (double.IsInfinity(availableSize.Height)) availableSize.Height = 600;
        return availableSize;
    }

    protected override Size ArrangeOverride(Size finalSize) => finalSize;

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        UpdateMetrics();
        RecalcSize();
    }

    // ------------------------------------------------------------- 绘制

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Brush(TerminalScreen.DefaultBg), null, new Rect(0, 0, ActualWidth, ActualHeight));

        var screen = _screen;
        if (screen == null)
        {
            DrawHint(dc, "集成 TUI 未启动");
            return;
        }

        var pixelsPerDip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        lock (screen.SyncRoot)
        {
            for (var row = 0; row < _rows; row++)
            {
                var line = screen.GetVisibleLine(row, _scrollOffset);
                if (line != null) DrawLine(dc, line, row, pixelsPerDip, screen);
            }

            var cursorRow = screen.CursorViewportRow(_scrollOffset);
            if (cursorRow >= 0 && screen.CursorVisible && IsFocused && _cursorOn)
                DrawCursor(dc, screen, cursorRow);
        }
    }

    private void DrawCursor(DrawingContext dc, TerminalScreen screen, int cursorRow)
    {
        var x = PadX + screen.CursorX * _cellW;
        var y = PadY + cursorRow * _cellH;
        var style = screen.CursorStyle;
        var shape = style switch
        {
            3 or 4 => 1,   // 下划线
            5 or 6 => 2,   // 竖条
            _ => 0,        // 方块
        };
        switch (shape)
        {
            case 1:
                dc.DrawRectangle(Brush(0xE6FFFFFF), null, new Rect(x, y + _cellH - 2, _cellW, 2));
                break;
            case 2:
                dc.DrawRectangle(Brush(0xE6FFFFFF), null, new Rect(x, y, 1.5, _cellH));
                break;
            default:
                dc.DrawRectangle(Brush(0x66FFFFFF), null, new Rect(x, y, _cellW, _cellH));
                break;
        }
    }

    private void DrawHint(DrawingContext dc, string text)
    {
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            _typeface, _fontSize, Brush(0xFF6B7280), VisualTreeHelper.GetDpi(this).PixelsPerDip);
        dc.DrawText(ft, new Point(PadX, PadY));
    }

    private void DrawLine(DrawingContext dc, TerminalCell[] line, int row, double pixelsPerDip, TerminalScreen screen)
    {
        var y = PadY + row * _cellH;
        var c = 0;
        while (c < _cols)
        {
            var cell = line[c];
            var attrs = (byte)(cell.Flags & TerminalCell.AttrMask);
            var fg = cell.Fg;
            var bg = cell.Bg;
            var link = cell.Link;
            var start = c;
            var text = new StringBuilder();
            while (c < _cols && line[c].Fg == fg && line[c].Bg == bg && line[c].Link == link
                   && (byte)(line[c].Flags & TerminalCell.AttrMask) == attrs)
            {
                if (line[c].Char != '\0')
                {
                    text.Append(line[c].Char);
                    if (line[c].Comb != '\0') text.Append(line[c].Comb);
                }
                c++;
            }

            var span = c - start;
            var fgColor = fg;
            var bgColor = bg;
            if ((attrs & TerminalCell.Reverse) != 0) (fgColor, bgColor) = (bgColor, fgColor);
            if ((attrs & TerminalCell.Bold) != 0) fgColor = Lighten(fgColor);
            if ((attrs & TerminalCell.Dim) != 0) fgColor = Scale(fgColor, 0.62);

            if (bgColor != TerminalScreen.DefaultBg)
                dc.DrawRectangle(Brush(bgColor), null,
                    new Rect(PadX + start * _cellW, y, span * _cellW, _cellH));

            var content = text.ToString();
            if (!string.IsNullOrWhiteSpace(content))
            {
                var bold = (attrs & TerminalCell.Bold) != 0;
                var italic = (attrs & TerminalCell.Italic) != 0;
                var typeface = bold ? _boldTypeface : italic ? _italicTypeface : _typeface;
                var ft = new FormattedText(content, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                    typeface, _fontSize, Brush(fgColor), pixelsPerDip);
                dc.DrawText(ft, new Point(PadX + start * _cellW, y));

                var underline = (attrs & TerminalCell.Underline) != 0;
                var hovered = link != 0 && screen.LinkAt(row, start, _scrollOffset) == _hoverLink;
                if (underline || hovered)
                    dc.DrawRectangle(Brush(fgColor), null,
                        new Rect(PadX + start * _cellW, y + ft.Baseline + 1.5, ft.WidthIncludingTrailingWhitespace, 1));
                if ((attrs & TerminalCell.Strike) != 0)
                    dc.DrawRectangle(Brush(fgColor), null,
                        new Rect(PadX + start * _cellW, y + ft.Baseline - ft.Height * 0.3, ft.WidthIncludingTrailingWhitespace, 1));
            }
        }
    }

    private SolidColorBrush Brush(uint argb)
    {
        if (_brushCache.TryGetValue(argb, out var brush)) return brush;
        var color = Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        brush = new SolidColorBrush(color);
        brush.Freeze();
        _brushCache[argb] = brush;
        return brush;
    }

    private static uint Lighten(uint argb)
    {
        var a = argb & 0xFF000000u;
        var r = Math.Min(255, ((argb >> 16) & 0xFF) * 1.25 + 18);
        var g = Math.Min(255, ((argb >> 8) & 0xFF) * 1.25 + 18);
        var b = Math.Min(255, (argb & 0xFF) * 1.25 + 18);
        return a | ((uint)r << 16) | ((uint)g << 8) | (uint)b;
    }

    private static uint Scale(uint argb, double factor)
    {
        var a = argb & 0xFF000000u;
        var r = (uint)(((argb >> 16) & 0xFF) * factor);
        var g = (uint)(((argb >> 8) & 0xFF) * factor);
        var b = (uint)((argb & 0xFF) * factor);
        return a | (r << 16) | (g << 8) | b;
    }

    // ------------------------------------------------------------- 输出

    private void OnOutputAvailable()
    {
        // 同步输出（DECSET 2026）：应用正在组帧，这一批输出不画，避免把半个帧画出来（撕裂/闪烁）。
        // 退出同步一般出现在**下一批**输出里——ConPTY 读取线程是「整块 Feed 完再通知」，
        // 因此同一块里若已含 ?2026l，这里读到的就是 false，正常重绘。
        if (IsSyncOutputActive())
        {
            _syncRedrawDeferred = true;
            Dispatcher.BeginInvoke(new Action(EnsureSyncFlushTimer));
            return;
        }
        ScheduleRedraw();
    }

    /// <summary>合并同一批输出里的多次重绘请求，顺带校正回滚偏移。</summary>
    private void ScheduleRedraw()
    {
        if (Interlocked.Exchange(ref _invalidatePending, 1) == 1) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() =>
        {
            Interlocked.Exchange(ref _invalidatePending, 0);
            _syncRedrawDeferred = false;
            ClampScrollOffsetToScreen();
            InvalidateVisual();
        }));
    }

    /// <summary>
    /// 备用屏幕没有可浏览的历史：应用在用备用屏幕（DECSET 1047/1049）时不允许把视口滚进主屏回滚缓冲，
    /// 否则会把历史行当成第 N 行画出来，与全屏 TUI 的实际内容对不上。
    /// </summary>
    private void ClampScrollOffsetToScreen()
    {
        var screen = _screen;
        if (screen == null) return;
        bool alt;
        int max;
        lock (screen.SyncRoot)
        {
            alt = screen.UsingAlternateScreen;
            max = screen.ScrollbackCount;
        }
        if (alt) { _scrollOffset = 0; return; }
        if (_scrollOffset > max) _scrollOffset = max;
    }

    /// <summary>应用是否正在"同步输出"组帧中（DECSET 2026，xterm 的 synchronized output）。</summary>
    private bool IsSyncOutputActive()
    {
        var screen = _screen;
        if (screen == null) return false;
        lock (screen.SyncRoot) return screen.SynchronizedOutput;
    }

    private void EnsureSyncFlushTimer()
    {
        if (!_syncFlushTimer.IsEnabled) _syncFlushTimer.Start();
    }

    private void OnProcessExited()
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            UpdateBlinkTimer();
            InvalidateVisual();
            Exited?.Invoke();
        }));
    }

    private void OnTitleChanged(string title)
    {
        Dispatcher.BeginInvoke(new Action(() => TitleChanged?.Invoke(title)));
    }

    private void OnClipboardRequested(string text)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try { Clipboard.SetText(text); } catch { /* 剪贴板被占用时忽略 */ }
        }));
    }

    private void UpdateBlinkTimer()
    {
        var screen = _screen;
        var shouldBlink = _session is { HasExited: false } && screen != null && screen.CursorBlink;
        if (shouldBlink && !_blinkTimer.IsEnabled)
        {
            _cursorOn = true;
            _blinkTimer.Start();
        }
        else if (!shouldBlink && _blinkTimer.IsEnabled)
        {
            _blinkTimer.Stop();
            _cursorOn = true;
        }
    }

    // ------------------------------------------------------------- 键盘

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var session = _session;
        if (session == null)
        {
            base.OnPreviewKeyDown(e);
            return;
        }
        var screen = _screen;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        // 粘贴：括号粘贴模式下 Ctrl+V 交给应用，否则按终端常规用 Ctrl+Shift+V / Shift+Insert
        if (key == Key.Insert && shift)
        {
            if (PasteFromClipboard()) e.Handled = true;
            else base.OnPreviewKeyDown(e);
            return;
        }
        if (key == Key.V && ctrl)
        {
            if (shift || (screen?.BracketedPaste ?? false))
            {
                if (PasteFromClipboard())
                {
                    e.Handled = true;
                    return;
                }
            }
        }

        string? sequence = null;
        if (screen?.Win32InputMode == true)
        {
            if (TryWin32Key(key, ctrl, shift, alt, out var record)) sequence = record;
        }
        else
        {
            sequence = MapClassicKey(key, ctrl, shift, alt, screen?.AppCursorKeys ?? false);
        }

        if (sequence != null)
        {
            if (sequence.Length > 0) session.Write(sequence);
            _scrollOffset = 0;
            e.Handled = true;
            InvalidateVisual();
            return;
        }
        base.OnPreviewKeyDown(e);
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        var session = _session;
        if (session != null && !string.IsNullOrEmpty(e.Text))
        {
            var screen = _screen;
            if (screen?.Win32InputMode == true)
            {
                // win32 输入模式：字符作为合成记录（Vk=0）上报，保持与按键记录同一通道
                foreach (var ch in e.Text)
                {
                    if (ch < ' ') continue;
                    session.Write(Win32Record(0, 0, ch, ControlState()));
                }
            }
            else
            {
                var sb = new StringBuilder(e.Text.Length);
                foreach (var ch in e.Text)
                    if (ch >= ' ') sb.Append(ch);
                if (sb.Length > 0) session.Write(sb.ToString());
            }
            _scrollOffset = 0;
            e.Handled = true;
            InvalidateVisual();
        }
        base.OnTextInput(e);
    }

    // -- win32 输入模式（DECSET 9001）：CSI Vk;Sc;Uc;Kd;Cs;Rc _ --------------

    private const int VkProcessKey = 0xE5;      // IME 合成键，交给 TextInput
    private const int VkPacket = 0xE7;

    private static string Win32Record(int vk, int sc, int uc, int cs)
        => $"\x1b[{vk};{sc};{uc};1;{cs};1_";

    private static int ControlState()
    {
        var m = Keyboard.Modifiers;
        var cs = 0;
        if ((m & ModifierKeys.Shift) != 0) cs |= 0x10;
        if ((m & ModifierKeys.Control) != 0) cs |= 0x08;
        if ((m & ModifierKeys.Alt) != 0) cs |= 0x02;
        return cs;
    }

    private static bool IsModifierKey(Key key) => key switch
    {
        Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin => true,
        _ => false,
    };

    /// <summary>该虚拟键在 win32 输入模式下是否有自己的名字（打印键以外的键）。</summary>
    private static bool IsNamedVk(int vk)
    {
        if (vk is 8 or 9 or 13 or 27 or 32) return true;
        if (vk is >= 33 and <= 40 or 45 or 46) return true;
        return vk is >= 112 and <= 123;
    }

    private static int NamedUc(int vk) => vk switch
    {
        13 => 13,   // Enter：Uc 与 Vk 一致，修饰位靠 Cs 传递
        9 => 9,
        8 => 8,
        27 => 27,
        32 => 32,
        _ => 0,
    };

    /// <summary>把当前按键编码成一条 win32 输入记录；返回 false 表示交给普通文本通道。</summary>
    private bool TryWin32Key(Key key, bool ctrl, bool shift, bool alt, out string record)
    {
        record = string.Empty;
        var vk = KeyInterop.VirtualKeyFromKey(key);
        if (vk == VkProcessKey || vk == VkPacket) return false;

        if (IsModifierKey(key))
        {
            // 裸修饰键：状态随下一个真实键的 Cs 传递，应用会丢弃其记录
            record = string.Empty;
            return true;
        }

        var sc = (int)(MapVirtualKey((uint)vk, 0) & 0xFF);
        var cs = ControlState();

        if (IsNamedVk(vk))
        {
            record = Win32Record(vk, sc, NamedUc(vk), cs);
            return true;
        }

        var isCharKey = IsCharacterVk(vk);
        if (!isCharKey) return false;
        if (!ctrl && !alt) return false;   // 普通/Shift 打印键走 TextInput，保留 IME 与死键

        var uc = 0;
        if (ctrl && vk is >= 65 and <= 90) uc = vk - 64;          // Ctrl+字母 → 控制码
        else if (alt && !ctrl) uc = BaseCharForVk(vk, shift);     // Alt+字母/符号 → 字符本体
        record = Win32Record(vk, sc, uc, cs);
        return true;
    }

    private static bool IsCharacterVk(int vk)
        => vk is >= 65 and <= 90 or >= 48 and <= 57 or 32
           or (>= 186 and <= 192) or (>= 219 and <= 222) or 226;

    private static char BaseCharForVk(int vk, bool shift)
    {
        if (vk is >= 65 and <= 90)
            return (char)(shift ? vk : vk + 32);
        if (vk is >= 48 and <= 57)
        {
            const string shifted = ")!@#$%^&*(";
            return shift ? shifted[vk - 48] : (char)vk;
        }
        if (vk == 32) return ' ';
        return vk switch
        {
            186 => shift ? ':' : ';',
            187 => shift ? '+' : '=',
            188 => shift ? '<' : ',',
            189 => shift ? '_' : '-',
            190 => shift ? '>' : '.',
            191 => shift ? '?' : '/',
            192 => shift ? '~' : '`',
            219 => shift ? '{' : '[',
            220 => shift ? '|' : '\\',
            221 => shift ? '}' : ']',
            222 => shift ? '"' : '\'',
            226 => '\\',
            _ => '\0',
        };
    }

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    // -- 经典 VT 编码 -------------------------------------------------------

    private static int XtermModifier()
    {
        var m = Keyboard.Modifiers;
        var mod = 1;
        if ((m & ModifierKeys.Shift) != 0) mod += 1;
        if ((m & ModifierKeys.Alt) != 0) mod += 2;
        if ((m & ModifierKeys.Control) != 0) mod += 4;
        return mod;
    }

    private static string? MapClassicKey(Key key, bool ctrl, bool shift, bool alt, bool appCursorKeys)
    {
        var mod = XtermModifier();

        // 带修饰的 CSI/SS3 键统一走 xterm 的 "1;<mod><final>" 形式
        string Csi(string final, bool ss3WhenPlain = false)
        {
            if (mod == 1) return (ss3WhenPlain ? "\x1bO" : "\x1b[") + final;
            return $"\x1b[1;{mod}{final}";
        }

        string Tilde(int code)
            => mod == 1 ? $"\x1b[{code}~" : $"\x1b[{code};{mod}~";

        switch (key)
        {
            case Key.Enter: return "\r";
            case Key.Back: return "\x7f";
            case Key.Tab: return shift ? "\x1b[Z" : "\t";
            case Key.Escape: return "\x1b";
            case Key.Up: return Csi("A", appCursorKeys);
            case Key.Down: return Csi("B", appCursorKeys);
            case Key.Right: return Csi("C", appCursorKeys);
            case Key.Left: return Csi("D", appCursorKeys);
            case Key.Home: return Csi("H", appCursorKeys);
            case Key.End: return Csi("F", appCursorKeys);
            case Key.PageUp: return Tilde(5);
            case Key.PageDown: return Tilde(6);
            case Key.Delete: return Tilde(3);
            case Key.Insert: return Tilde(2);
            case Key.F1: return Csi("P", true);
            case Key.F2: return Csi("Q", true);
            case Key.F3: return Csi("R", true);
            case Key.F4: return Csi("S", true);
            case Key.F5: return Tilde(15);
            case Key.F6: return Tilde(17);
            case Key.F7: return Tilde(18);
            case Key.F8: return Tilde(19);
            case Key.F9: return Tilde(20);
            case Key.F10: return Tilde(21);
            case Key.F11: return Tilde(23);
            case Key.F12: return Tilde(24);
        }

        if (ctrl && !alt && key >= Key.A && key <= Key.Z)
            return ((char)(key - Key.A + 1)).ToString();
        if (ctrl && key >= Key.A && key <= Key.Z && alt)
            return "\x1b" + (char)(key - Key.A + 1);

        // Alt+可打印字符：ESC 前缀（由 TextInput 补上字符本体）
        if (alt && !ctrl && key >= Key.A && key <= Key.Z)
            return "\x1b" + (shift ? (char)key : char.ToLowerInvariant((char)key));

        return null;
    }

    // ------------------------------------------------------------- 鼠标

    private (int Col, int Row)? CellAt(Point p)
    {
        if (_cellW <= 0 || _cellH <= 0) return null;
        var col = (int)Math.Floor((p.X - PadX) / _cellW);
        var row = (int)Math.Floor((p.Y - PadY) / _cellH);
        if (col < 0 || row < 0 || col >= _cols || row >= _rows) return null;
        return (col, row);
    }

    /// <summary>把鼠标事件编码成应用启用的上报格式；未启用上报时返回 false。</summary>
    private bool ReportMouse(MouseButton? button, bool release, bool motion, int wheel, Point position)
    {
        var screen = _screen;
        var session = _session;
        if (screen == null || session == null || screen.MouseMode == 0) return false;

        // 按住 Shift 时保留本地选择/滚动（与主流终端一致）
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) return false;

        // 上报坐标是屏幕坐标：回滚状态下先回到实时屏幕，避免把历史行当成第 N 行
        if (_scrollOffset != 0)
        {
            _scrollOffset = 0;
            InvalidateVisual();
        }

        var cell = CellAt(position);
        if (cell == null) return false;
        var (col, row) = cell.Value;

        if (motion)
        {
            // 1000 只报按键；1002 加拖动；1003 加全部移动（悬停）
            var dragging = button != null;
            if (screen.MouseMode == 1000) return false;
            if (screen.MouseMode == 1002 && !dragging) return false;
        }

        var mod = 0;
        if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) mod |= 4;
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) mod |= 8;
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) mod |= 16;

        int code;
        if (wheel != 0)
        {
            code = wheel > 0 ? 64 : 65;
        }
        else
        {
            code = button switch
            {
                MouseButton.Left => 0,
                MouseButton.Middle => 1,
                MouseButton.Right => 2,
                _ => 3,
            };
            if (motion) code |= 32;
        }
        code |= mod;

        if (screen.MouseSgrPixels)
        {
            // 1016：SGR 形式，但坐标改成**设备像素**（相对文本区左上角，1 起）
            var dpi = VisualTreeHelper.GetDpi(this);
            var px = Math.Max(1, (int)Math.Round((position.X - PadX) * dpi.DpiScaleX) + 1);
            var py = Math.Max(1, (int)Math.Round((position.Y - PadY) * dpi.DpiScaleY) + 1);
            var finalPx = release && wheel == 0 ? 'm' : 'M';
            session.Write($"\x1b[<{code};{px};{py}{finalPx}");
        }
        else if (screen.MouseSgr)
        {
            var final = release && wheel == 0 ? 'm' : 'M';
            session.Write($"\x1b[<{code};{col + 1};{row + 1}{final}");
        }
        else if (screen.MouseUrxvt)
        {
            // 1015（urxvt）：CSI Cb ; Cx ; Cy M，十进制、1 起；释放同样用按键 3
            if (release && wheel == 0) code = 3 | mod;
            session.Write($"\x1b[{32 + code};{col + 1};{row + 1}M");
        }
        else
        {
            // 传统 X10 编码：常量化到可打印区间；释放用按键 3
            if (release && wheel == 0) code = 3 | mod;
            if (screen.MouseUtf8)
            {
                // 1005：这三个值按 UTF-8 码点写出（坐标 >95 时必须走多字节，故不能用 (char) 截断）
                session.Write($"\x1b[M{Utf8MouseValue(code)}{Utf8MouseValue(col + 1)}{Utf8MouseValue(row + 1)}");
            }
            else
            {
                var b = (char)Math.Clamp(32 + code, 32, 255);
                var x = (char)Math.Clamp(32 + col + 1, 32, 255);
                var y = (char)Math.Clamp(32 + row + 1, 32, 255);
                session.Write($"\x1b[M{b}{x}{y}");
            }
        }
        _scrollOffset = 0;
        return true;
    }

    /// <summary>
    /// 1005（UTF-8 鼠标）的单个值：先加 32 偏移，再作为码点编码。
    /// ConPTY 写入走 UTF-8（<see cref="ConPtySession.Write"/>），所以 &gt;0x7F 的值会自然变成多字节。
    /// </summary>
    private static string Utf8MouseValue(int value)
        => char.ConvertFromUtf32(Math.Clamp(32 + value, 32, 0x10FFFF));

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var position = e.GetPosition(this);
        var cell = CellAt(position);

        var screen = _screen;
        var link = cell == null ? null : screen?.LinkAt(cell.Value.Row, cell.Value.Col, _scrollOffset);
        if (!string.Equals(link, _hoverLink, StringComparison.Ordinal))
        {
            _hoverLink = link;
            Cursor = link != null ? Cursors.Hand : (screen?.MouseMode != 0 ? Cursors.Arrow : Cursors.IBeam);
            InvalidateVisual();
        }

        var dragging = e.LeftButton == MouseButtonState.Pressed ? MouseButton.Left
            : e.MiddleButton == MouseButtonState.Pressed ? MouseButton.Middle
            : e.RightButton == MouseButtonState.Pressed ? MouseButton.Right
            : (MouseButton?)null;
        if (ReportMouse(dragging, release: false, motion: true, wheel: 0, position)) e.Handled = true;
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        var position = e.GetPosition(this);
        if (ReportMouse(e.ChangedButton, release: false, motion: false, wheel: 0, position))
        {
            e.Handled = true;
            base.OnMouseDown(e);
            return;
        }

        // 未开启鼠标上报时：Ctrl+点击 打开 OSC 8 链接
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            var cell = CellAt(position);
            var link = cell == null ? null : _screen?.LinkAt(cell.Value.Row, cell.Value.Col, _scrollOffset);
            if (!string.IsNullOrEmpty(link)) OpenLink(link!);
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (ReportMouse(e.ChangedButton, release: true, motion: false, wheel: 0, e.GetPosition(this)))
        {
            e.Handled = true;
            base.OnMouseUp(e);
            return;
        }
        base.OnMouseUp(e);
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        var position = e.GetPosition(this);
        if (ReportMouse(null, release: false, motion: false, wheel: e.Delta, position))
        {
            e.Handled = true;
            return;
        }

        var screen = _screen;
        if (screen == null) return;
        var step = e.Delta > 0 ? 3 : -3;
        int max;
        // 备用屏幕下没有可浏览的历史（max=0）：全屏 TUI 里滚轮应交给应用（已在上面的
        // ReportMouse 里上报），而不是让渲染层滚进主屏回滚缓冲。
        lock (screen.SyncRoot) max = screen.UsingAlternateScreen ? 0 : screen.ScrollbackCount;
        _scrollOffset = Math.Clamp(_scrollOffset + step, 0, max);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (_hoverLink != null)
        {
            _hoverLink = null;
            Cursor = _screen?.MouseMode != 0 ? Cursors.Arrow : Cursors.IBeam;
            InvalidateVisual();
        }
        base.OnMouseLeave(e);
    }

    private static void OpenLink(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // 无效链接或没有关联程序：忽略
        }
    }

    // ------------------------------------------------------------- 焦点

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        _cursorOn = true;
        UpdateBlinkTimer();
        if (_screen?.FocusEvents == true) _session?.Write("\x1b[I");
        InvalidateVisual();
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        _blinkTimer.Stop();
        if (_screen?.FocusEvents == true) _session?.Write("\x1b[O");
        InvalidateVisual();
    }
}
