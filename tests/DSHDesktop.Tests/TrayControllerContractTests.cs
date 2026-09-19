using System;
using System.IO;
using Xunit;

namespace DSHDesktop.Tests;

public class TrayControllerContractTests
{
    [Fact]
    public void MainWindow_DelegatesTrayOwnershipAndWindowVisibility()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));

        Assert.Contains("readonly TrayController _trayController", source);
        Assert.Contains("_trayController.Hide()", source);
        Assert.Contains("_trayController.Restore()", source);
        Assert.Contains("_trayController.RecordWindowState(", source);
        Assert.Contains("_trayController.Dispose()", source);
        Assert.DoesNotContain("WinForms.NotifyIcon", source);
        Assert.DoesNotContain("TrayMenu.Create()", source);
        Assert.DoesNotContain("LoadIcoFrame(", source);
        Assert.DoesNotContain("DestroyIcon(", source);
    }

    [Fact]
    public void Hide_ShowsTrayIconBeforeHidingWindow()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("TrayController.cs"));
        var declaration = source.IndexOf("public bool Hide()", StringComparison.Ordinal);
        Assert.True(declaration >= 0, "找不到托盘隐藏入口");
        var body = SourceScanner.MethodBody(source, declaration);

        var ensure = body.IndexOf("EnsureIcon();", StringComparison.Ordinal);
        var unavailableGuard = body.IndexOf("if (_tray == null)", StringComparison.Ordinal);
        var showIcon = body.IndexOf("_tray.Visible = true;", StringComparison.Ordinal);
        var hideWindow = body.IndexOf("_window.Hide();", StringComparison.Ordinal);

        Assert.True(ensure >= 0 && unavailableGuard > ensure, "隐藏前必须创建并验证托盘图标");
        Assert.True(showIcon > unavailableGuard, "必须先让托盘图标可见");
        Assert.True(hideWindow > showIcon, "只有托盘图标显示成功后才能隐藏窗口");
    }

    [Fact]
    public void Dispose_ReleasesManagedMenuAndNativeIconHandle()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("TrayController.cs"));
        var declaration = source.IndexOf("public void Dispose()", StringComparison.Ordinal);
        Assert.True(declaration >= 0, "找不到托盘资源释放入口");
        var body = SourceScanner.MethodBody(source, declaration);

        Assert.Contains("tray.Visible = false", body);
        Assert.Contains("tray.Dispose()", body);
        Assert.Contains("_menu.Dispose()", body);
        Assert.Contains("DestroyIcon(_iconHandle)", body);
    }
}
