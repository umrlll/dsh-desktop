using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Xunit;

namespace DSHDesktop.Tests;

/// <summary>
/// <c>DesktopLog.cs</c> 的脱敏规则、阈值行为、单条记录上限与诊断包导出。
///
/// 该组件是进程级单例（静态 <c>_initialized</c>/<c>_dir</c>），<c>Initialize</c> 对同一进程
/// 只生效一次，因此这里用静态门闩保证"整个测试进程只初始化一次"，并把日志目录指向
/// <c>%TEMP%</c> 下的专属目录，绝不触碰用户真实的日志目录。
/// 测试程序集已关闭并行（见 <c>AssemblyInfo.cs</c>），所以静态状态不会互相穿插。
///
/// 写"标记串"时必须注意：脱敏规则会把 32 位以上的 hex 串、40 位以上的 base64 风格串
/// 整体换成 <c>[已脱敏]</c>（DesktopLog.cs:384-385）。因此标记串一律用
/// <c>MK-&lt;12 位 hex&gt;</c> 这种短片段，否则测试会因为"自己的标记被遮掉"而假失败。
/// </summary>
public class DesktopLogTests
{
    private static readonly string LogDir =
        Path.Combine(Path.GetTempPath(), "dsh-desktop-tests-" + Guid.NewGuid().ToString("N"));

    private static readonly object InitGate = new();
    private static bool _initialized;

    public DesktopLogTests()
    {
        lock (InitGate)
        {
            if (_initialized) return;
            Directory.CreateDirectory(LogDir);
            DesktopLog.Initialize(LogDir);   // 只生效一次；此后 _dir 恒为 LogDir
            _initialized = true;
        }
    }

    private static string DayFile()
        => Path.Combine(LogDir, "dsh-" + DateTime.Now.ToString("yyyy-MM-dd") + ".log");

    /// <summary>短标记串：不会被脱敏规则命中（见类注释）。</summary>
    private static string Marker() => "MK-" + Guid.NewGuid().ToString("N")[..12];

    /// <summary>超过 64KB 且不会被脱敏规则命中的长串（片段 2 字符，避开 hex/base64 规则）。</summary>
    private static string LongPayload() => string.Concat(Enumerable.Repeat("ab-", 30000));

    // ---------------------------------------------------------------- 脱敏

    [Theory]
    [InlineData("https://api.example.com/v1/x?token=SECRETVALUE123", "token=[已脱敏]")]
    [InlineData("Authorization: Bearer abcdefghijklmnopqrst", "Bearer [已脱敏]")]
    [InlineData("password=hunter2hunter2", "password=[已脱敏]")]
    [InlineData("sk-abcdefghijklmnopqrst", "[已脱敏]")]
    [InlineData("digest=0123456789abcdef0123456789abcdef", "[已脱敏]")]
    public void Mask_RedactsKnownSecretShapes(string input, string expectedFragment)
    {
        var masked = DesktopLog.Mask(input);

        Assert.Contains(expectedFragment, masked);
    }

    [Fact]
    public void Mask_JsonApiKey_KeepsFieldNameButHidesValue()
    {
        var masked = DesktopLog.Mask("{\"apiKey\":\"supersecretvalue\"}");

        Assert.Contains("\"apiKey\":\"[已脱敏]\"", masked);
        Assert.DoesNotContain("supersecretvalue", masked);
    }

    [Fact]
    public void Mask_EmptyOrNull_IsReturnedAsIs()
    {
        Assert.Equal("", DesktopLog.Mask(""));
        Assert.Null(DesktopLog.Mask(null!));
    }

    [Theory]
    [InlineData("http://127.0.0.1:3080/?token=abc", "http://127.0.0.1:3080/?token=[已脱敏]")]
    [InlineData("http://h/p?x=1&token=abc", "http://h/p?x=1&token=[已脱敏]")]
    [InlineData("http://h/p", "http://h/p")]
    public void RedactUrl_RemovesOnlyTheTokenValue(string input, string expected)
    {
        Assert.Equal(expected, DesktopLog.RedactUrl(input));
    }

    [Fact]
    public void Write_AppliesMaskingBeforeHittingDisk()
    {
        DesktopLog.Info("GET https://api.example.com/x?token=SECRETVALUE123");

        var text = File.ReadAllText(DayFile());
        Assert.Contains("token=[已脱敏]", text);          // 值被遮
        Assert.Contains("api.example.com", text);        // 上下文仍在，日志仍可读
        Assert.DoesNotContain("SECRETVALUE123", text);
    }

    // ---------------------------------------------------------------- 阈值与上限

    [Fact]
    public void Write_DropsRecordsBelowThreshold_ButKeepsThemAtOrAbove()
    {
        var dropped = Marker();
        var previous = DesktopLog.Threshold;
        try
        {
            DesktopLog.Threshold = LogLevel.Error;
            DesktopLog.Info(dropped);
            DesktopLog.Warn(dropped);   // Warn < Error，同样应被丢弃
        }
        finally
        {
            DesktopLog.Threshold = previous;
        }

        Assert.DoesNotContain(dropped, File.ReadAllText(DayFile()));

        // 阳性对照：阈值恢复后同一条记录必须落盘——否则上面的"没找到"可能只是写失败
        DesktopLog.Warn(dropped);
        Assert.Contains(dropped, File.ReadAllText(DayFile()));
    }

    [Fact]
    public void Write_TruncatesSingleRecordAt64Kb()
    {
        var payload = LongPayload();
        Assert.True(payload.Length > 64 * 1024);

        DesktopLog.Info(payload);

        var text = File.ReadAllText(DayFile());
        Assert.Contains("…(截断)", text);
        Assert.Contains(payload[..1024], text);            // 前 64KB 保住了
        Assert.DoesNotContain(payload, text);              // 整条没有落盘
    }

    // ---------------------------------------------------------------- 诊断包导出

    [Fact]
    public void ExportDiagnostics_IncludesLogsAndSystemInfo()
    {
        DesktopLog.Info(Marker());
        var zip = Path.Combine(LogDir, "diag-" + Guid.NewGuid().ToString("N") + ".zip");

        var result = DesktopLog.ExportDiagnostics(zip);

        Assert.Equal(zip, result);
        Assert.True(File.Exists(zip));
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("system-info.txt", names);
        Assert.Contains(names, n => n.StartsWith("dsh-", StringComparison.Ordinal)
                                    && n.EndsWith(".log", StringComparison.Ordinal));

        var info = archive.GetEntry("system-info.txt")!;
        using var reader = new StreamReader(info.Open());
        var text = reader.ReadToEnd();
        Assert.Contains("日志条数", text);
    }

    [Fact]
    public void ExportDiagnostics_RespectsByteBudget()
    {
        DesktopLog.Info(Marker());
        var zip = Path.Combine(LogDir, "diag-budget-" + Guid.NewGuid().ToString("N") + ".zip");

        // 预算 0：任何日志文件都进不去，但仍然要产出一个只含 system-info.txt 的包
        var result = DesktopLog.ExportDiagnostics(zip, maxBytes: 0);

        Assert.Equal(zip, result);
        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(new[] { "system-info.txt" }, archive.Entries.Select(e => e.FullName).ToArray());
    }

    [Fact]
    public void Describe_Exception_IsSingleLineWithTypeAndMessage()
    {
        var text = DesktopLog.Describe(new InvalidOperationException("boom"));

        Assert.StartsWith("InvalidOperationException: boom", text);
        Assert.DoesNotContain("\n", text);
    }
}
