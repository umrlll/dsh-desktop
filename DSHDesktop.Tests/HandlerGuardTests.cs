using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace DSHDesktop.Tests;

/// <summary>
/// 结构测试（源码扫描，不需要 WPF 宿主）：`DSHDesktop/**` 里每个 <c>async void</c> 事件处理器
/// 都必须自带 <c>catch</c>，且 <c>StartAndEmbedAsync</c> 必须只是「转调内核 + catch」的包装。
///
/// 为什么这条契约值得锁：<c>async void</c> 的异常没有调用方能接住，只会冒到
/// <c>App.xaml.cs</c> 的 <c>DispatcherUnhandledException</c> —— 那里是「弹框 + <c>Shutdown(1)</c>」。
/// 于是任何一处漏掉的 <c>catch</c> 都会把「一次动作失败」升级成「整个应用退出」。
///
/// 局限（明确记录）：它只断言 <c>catch</c> 存在，**不**断言该 catch 可达、也不断言异常被正确上报；
/// 后两者需要 WPF 宿主才能测，属于当前测试工程的已知盲区。
/// </summary>
public class HandlerGuardTests
{
    [Fact]
    public void EveryAsyncVoidHandler_HasItsOwnCatch()
    {
        var scanned = new List<string>();
        var unguarded = new List<string>();

        foreach (var path in SourceScanner.ProductSources())
        {
            var source = File.ReadAllText(path);
            foreach (Match m in Regex.Matches(source, @"private\s+async\s+void\s+(?<name>\w+)\s*\("))
            {
                var name = m.Groups["name"].Value;
                scanned.Add(name);

                // 必须先在"只剩代码"的投影上判定：注释或字面量里出现 catch 不算兜底
                var code = SourceScanner.StripCommentsAndStrings(SourceScanner.MethodBody(source, m.Index));
                if (!Regex.IsMatch(code, @"\bcatch\s*(\(|\{|when\b)"))
                    unguarded.Add(Path.GetFileName(path) + ":" + name);
            }
        }

        // 扫描器自身失效（正则写错、文件挪走）时必须失败，而不是"一个都没扫到所以全绿"
        Assert.Contains("OnToggleServer", scanned);
        Assert.Contains("OnCheckUpdate", scanned);
        Assert.Contains("OnApplyUpdate", scanned);
        Assert.Contains("OnMenuRestartToRecovery", scanned);
        Assert.Contains("Window_Loaded", scanned);

        Assert.Empty(unguarded);
    }

    [Fact]
    public void StartAndEmbedAsync_IsOnlyAGuardedWrapper()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));

        var index = source.IndexOf("private async Task StartAndEmbedAsync(", System.StringComparison.Ordinal);
        Assert.True(index >= 0, "找不到 StartAndEmbedAsync");

        var body = SourceScanner.StripCommentsAndStrings(SourceScanner.MethodBody(source, index));
        Assert.Matches(@"\bcatch\s*\(", body);             // 有兜底
        Assert.Contains("StartAndEmbedCoreAsync", body);   // 且只转调内核
        Assert.DoesNotContain("Process.Start", body);      // 没把实现又搬回包装里
    }
}
