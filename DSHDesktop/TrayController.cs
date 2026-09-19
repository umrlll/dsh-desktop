using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using WinForms = System.Windows.Forms;

namespace DSHDesktop;

/// <summary>托盘菜单能够调用的窗口命令与动态状态。</summary>
internal sealed class TrayCommands
{
    public required Func<bool> IsServerRunning { get; init; }
    public required Func<string?> CurrentAuthUrl { get; init; }
    public required Func<bool> StopServerOnExit { get; init; }
    public required Action ToggleServer { get; init; }
    public required Action RestartServer { get; init; }
    public required Action ReloadRenderer { get; init; }
    public required Action OpenExternal { get; init; }
    public required Action CopyAuthUrl { get; init; }
    public required Action CheckUpdate { get; init; }
    public required Action OpenLogs { get; init; }
    public required Action ExportDiagnostics { get; init; }
    public required Action Exit { get; init; }
    public required Action ClosePopups { get; init; }
    public required Action FocusContent { get; init; }
    public required Action<string> SetStatus { get; init; }
}

/// <summary>
/// Owns the Windows notification icon, menu, hide/restore state, icon handle, and disposal.
/// Business operations are supplied as commands so this shell adapter does not own server/update state.
/// </summary>
internal sealed class TrayController : IDisposable
{
    private readonly Window _window;
    private readonly TrayCommands _commands;
    private WinForms.NotifyIcon? _tray;
    private WinForms.ContextMenuStrip? _menu;
    private WinForms.ToolStripMenuItem? _toggleItem;
    private WinForms.ToolStripMenuItem? _copyUrlItem;
    private WinForms.ToolStripMenuItem? _exitItem;
    private bool _hidden;
    private bool _tipShown;
    private bool _disposed;
    private IntPtr _iconHandle;

    public TrayController(Window window, TrayCommands commands)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _commands = commands ?? throw new ArgumentNullException(nameof(commands));
    }

    public bool IsHidden => _hidden;
    public WindowState RestoreState { get; private set; } = WindowState.Normal;

    public void RecordWindowState(WindowState state)
    {
        if (state != WindowState.Minimized) RestoreState = state;
    }

    /// <summary>
    /// Shows the icon before hiding the window. If icon creation/display fails, the window stays
    /// visible so the user can never lose the only recovery and exit surface.
    /// </summary>
    public bool Hide()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_hidden) return true;

        try { _commands.ClosePopups(); } catch { /* nonessential */ }

        try { EnsureIcon(); }
        catch (Exception ex) { DesktopLog.Error("创建托盘图标失败，已取消收起到托盘", ex); }

        if (_tray == null)
        {
            _commands.SetStatus("托盘图标不可用，已取消收起到托盘（窗口保持打开）。");
            return false;
        }

        try { _tray.Visible = true; }
        catch (Exception ex)
        {
            DesktopLog.Error("显示托盘图标失败，已取消收起到托盘", ex);
            _commands.SetStatus("托盘图标不可用，已取消收起到托盘（窗口保持打开）。");
            return false;
        }

        _hidden = true;
        _window.Hide();
        ShowFirstHideTip();
        return true;
    }

    public bool Restore()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_hidden) return false;

        _hidden = false;
        if (_tray != null) _tray.Visible = false;

        _window.Show();
        _window.WindowState = RestoreState == WindowState.Maximized
            ? WindowState.Maximized
            : WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Dispatcher.BeginInvoke(
            _commands.FocusContent,
            System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        return true;
    }

    private void ShowFirstHideTip()
    {
        if (_tipShown || _tray == null) return;
        _tipShown = true;
        try
        {
            _tray.ShowBalloonTip(
                2500,
                "DSH Desktop",
                "已收起到托盘，点击托盘图标可恢复窗口；退出请用托盘菜单「退出」。",
                WinForms.ToolTipIcon.Info);
        }
        catch { /* 气泡提示失败不影响功能 */ }
    }

    private void EnsureIcon()
    {
        if (_tray != null) return;

        var menu = TrayMenu.Create();
        _menu = menu;
        menu.Items.Add(TrayMenu.Item("显示主窗口", 0xE8A7, () => Restore(), "把主窗口带回前台"));

        menu.Items.Add(TrayMenu.Separator());
        _toggleItem = TrayMenu.Item(
            "停止 DSH 服务",
            0xE71A,
            _commands.ToggleServer,
            "启动或停止本窗口托管的 dsh web");
        menu.Items.Add(_toggleItem);
        menu.Items.Add(TrayMenu.Item(
            "重启服务",
            0xE72C,
            _commands.RestartServer,
            "停掉托管的 dsh 并重新拉起、重新内嵌（等价于原地重建一次会话）"));
        menu.Items.Add(TrayMenu.Item(
            "重载界面",
            0xE895,
            _commands.ReloadRenderer,
            "重新加载内嵌页面，不重启服务"));

        menu.Items.Add(TrayMenu.Separator());
        menu.Items.Add(TrayMenu.Item(
            "在浏览器中打开",
            0xE774,
            _commands.OpenExternal,
            "用系统默认浏览器打开当前服务地址"));
        _copyUrlItem = TrayMenu.Item(
            "复制访问地址",
            0xE8C8,
            _commands.CopyAuthUrl,
            "把带鉴权 token 的地址复制到剪贴板（服务未就绪时不可用）");
        menu.Items.Add(_copyUrlItem);
        menu.Items.Add(TrayMenu.Item(
            "检查更新",
            0xEA8F,
            _commands.CheckUpdate,
            "查询 DSH 是否有新版本"));

        menu.Items.Add(TrayMenu.Separator());
        menu.Items.Add(TrayMenu.Item(
            "打开日志目录",
            0xE7C3,
            _commands.OpenLogs,
            "启动失败时先看这里"));
        menu.Items.Add(TrayMenu.Item(
            "导出诊断…",
            0xE896,
            _commands.ExportDiagnostics,
            "打包最近日志与系统信息摘要"));

        menu.Items.Add(TrayMenu.Separator());
        _exitItem = TrayMenu.Item(
            "退出",
            0xE7E8,
            _commands.Exit,
            ExitTooltip());
        menu.Items.Add(_exitItem);
        menu.Opening += OnMenuOpening;

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = "DSH Desktop",
            ContextMenuStrip = menu,
            Visible = false,
        };
        _tray.MouseClick += OnTrayMouseClick;
        _tray.DoubleClick += OnTrayDoubleClick;
    }

    private void OnMenuOpening(object? sender, CancelEventArgs e) => SyncMenu();

    private void OnTrayMouseClick(object? sender, WinForms.MouseEventArgs e)
    {
        if (e.Button == WinForms.MouseButtons.Left) Restore();
    }

    private void OnTrayDoubleClick(object? sender, EventArgs e) => Restore();

    private void SyncMenu()
    {
        try
        {
            var running = _commands.IsServerRunning();
            if (_toggleItem != null)
            {
                TrayMenu.Restyle(
                    _toggleItem,
                    running ? "停止 DSH 服务" : "启动 DSH 服务",
                    running ? 0xE71Au : 0xE768u);
            }

            if (_copyUrlItem != null)
            {
                var ready = !string.IsNullOrWhiteSpace(_commands.CurrentAuthUrl());
                _copyUrlItem.Enabled = ready;
                _copyUrlItem.ToolTipText = ready
                    ? "把带鉴权 token 的地址复制到剪贴板"
                    : "服务尚未就绪，稍后再试";
            }

            if (_exitItem != null) _exitItem.ToolTipText = ExitTooltip();
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("刷新托盘菜单失败: " + DesktopLog.Describe(ex));
        }
    }

    private string ExitTooltip()
        => _commands.StopServerOnExit()
            ? "退出并停止服务"
            : "退出（保留服务继续运行）";

    /// <summary>DSH.ico uses PNG-compressed frames, so choose and decode the closest frame.</summary>
    private System.Drawing.Icon LoadIcon()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "DSH.ico");
            if (File.Exists(path))
            {
                var size = Math.Max(16, WinForms.SystemInformation.SmallIconSize.Width);
                using var bitmap = LoadIcoFrame(path, size);
                if (bitmap != null)
                {
                    _iconHandle = bitmap.GetHicon();
                    return System.Drawing.Icon.FromHandle(_iconHandle);
                }
            }
        }
        catch { /* fall back to the system application icon */ }
        return System.Drawing.SystemIcons.Application;
    }

    private static System.Drawing.Bitmap? LoadIcoFrame(string path, int size)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 6) return null;
        var count = BitConverter.ToUInt16(bytes, 4);
        var bestOffset = -1;
        var bestLength = 0;
        var bestDiff = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var entry = 6 + i * 16;
            if (entry + 16 > bytes.Length) break;
            var width = bytes[entry] == 0 ? 256 : bytes[entry];
            var length = (int)BitConverter.ToUInt32(bytes, entry + 8);
            var offset = (int)BitConverter.ToUInt32(bytes, entry + 12);
            if (length <= 0 || offset < 0 || offset + length > bytes.Length) continue;
            var diff = Math.Abs(width - size);
            if (diff >= bestDiff) continue;
            bestDiff = diff;
            bestOffset = offset;
            bestLength = length;
        }
        if (bestOffset < 0) return null;
        using var stream = new MemoryStream(bytes, bestOffset, bestLength);
        return new System.Drawing.Bitmap(stream);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        var tray = _tray;
        _tray = null;
        if (tray != null)
        {
            try
            {
                tray.MouseClick -= OnTrayMouseClick;
                tray.DoubleClick -= OnTrayDoubleClick;
                tray.Visible = false;
                tray.Icon = null;
                tray.Dispose();
            }
            catch { /* shutdown remains best-effort */ }
        }

        if (_menu != null)
        {
            try
            {
                _menu.Opening -= OnMenuOpening;
                _menu.Dispose();
            }
            catch { /* shutdown remains best-effort */ }
            _menu = null;
        }

        if (_iconHandle != IntPtr.Zero)
        {
            try { DestroyIcon(_iconHandle); } catch { /* ignore */ }
            _iconHandle = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
