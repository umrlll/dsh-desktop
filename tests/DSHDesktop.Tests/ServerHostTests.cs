using System.Diagnostics;
using System.Text.RegularExpressions;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class ServerHostTests
{
    [Fact]
    public void NewHost_IsStoppedAndReadyForFirstStart()
    {
        using var host = new ServerHost();

        Assert.Equal(ServerHostPhase.Stopped, host.Snapshot.Phase);
        Assert.False(host.IsRunning);
        Assert.True(host.ReadinessCompleted);
        Assert.Equal(0, host.Generation);
    }

    [Theory]
    [InlineData("dsh web: http://127.0.0.1:43123/?token=secret", true, "http://127.0.0.1:43123/?token=secret")]
    [InlineData("prefix dsh web: https://127.0.0.1:43123/ suffix", true, "https://127.0.0.1:43123/")]
    [InlineData("ordinary diagnostic line", false, "")]
    public void TryExtractEndpoint_UsesFirstCaptureGroup(string line, bool expected, string endpoint)
    {
        var pattern = new Regex(@"dsh web:\s*(https?://\S+)");

        Assert.Equal(expected, ServerHost.TryExtractEndpoint(pattern, line, out var actual));
        Assert.Equal(endpoint, actual);
    }

    [Theory]
    [InlineData(ServerHostPhase.Stopped, false, true)]
    [InlineData(ServerHostPhase.Starting, true, false)]
    [InlineData(ServerHostPhase.Ready, true, true)]
    [InlineData(ServerHostPhase.TimedOut, true, true)]
    [InlineData(ServerHostPhase.Stopping, true, true)]
    [InlineData(ServerHostPhase.Exited, false, true)]
    [InlineData(ServerHostPhase.Failed, false, true)]
    public void Snapshot_ExposesStableLifecycleFlags(
        ServerHostPhase phase,
        bool isRunning,
        bool readinessCompleted)
    {
        var snapshot = new ServerHostSnapshot(3, phase);

        Assert.Equal(isRunning, snapshot.IsRunning);
        Assert.Equal(readinessCompleted, snapshot.ReadinessCompleted);
    }

    [Theory]
    [InlineData(null, false, "exit=不可用")]
    [InlineData(0, false, "exit=0 (0x00000000)")]
    [InlineData(-1073741819, false, "exit=-1073741819 (0xC0000005)")]
    public void FormatExitCode_PreservesSignedAndHexRepresentations(
        int? exitCode,
        bool isRunning,
        string expected)
    {
        Assert.Equal(expected, ServerHost.FormatExitCode(exitCode, isRunning));
    }

    [Fact]
    public async Task StartAsync_ReturnsStableFailureInsteadOfThrowing()
    {
        using var host = new ServerHost();
        var startInfo = new ProcessStartInfo("dshdesktop-file-that-does-not-exist-7f39.exe")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        var result = await host.StartAsync(
            startInfo,
            new Regex(@"READY:(\S+)"),
            TimeSpan.FromSeconds(1));

        Assert.Equal(ServerStartOutcome.FailedToStart, result.Outcome);
        Assert.Equal(ServerHostPhase.Failed, result.Snapshot.Phase);
        Assert.False(host.IsRunning);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    [Fact]
    public async Task StartAsync_DetectsEndpointAndStopTerminatesOwnedProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new ServerHost();
        var startInfo = CommandShell(
            "echo dsh web: http://127.0.0.1:43123/?token=test & ping -n 6 127.0.0.1 >nul");

        var result = await host.StartAsync(
            startInfo,
            new Regex(@"dsh web:\s*(https?://\S+)"),
            TimeSpan.FromSeconds(5));

        Assert.Equal(ServerStartOutcome.Ready, result.Outcome);
        Assert.Equal("http://127.0.0.1:43123/?token=test", result.Endpoint);
        Assert.True(host.IsRunning);

        host.Stop(TimeSpan.FromSeconds(5));

        Assert.Equal(ServerHostPhase.Stopped, host.Snapshot.Phase);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task StartAsync_ReportsExitBeforeReadiness()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new ServerHost();

        var result = await host.StartAsync(
            CommandShell("exit /b 17"),
            new Regex(@"READY:(\S+)"),
            TimeSpan.FromSeconds(5));

        Assert.Equal(ServerStartOutcome.ExitedBeforeReady, result.Outcome);
        Assert.Equal(ServerHostPhase.Exited, result.Snapshot.Phase);
        Assert.Equal(17, result.Snapshot.ExitCode);
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task StartAsync_TimesOutWithoutLosingStopControl()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new ServerHost();

        var result = await host.StartAsync(
            CommandShell("ping -n 6 127.0.0.1 >nul"),
            new Regex(@"READY:(\S+)"),
            TimeSpan.FromMilliseconds(100));

        Assert.Equal(ServerStartOutcome.TimedOut, result.Outcome);
        Assert.Equal(ServerHostPhase.TimedOut, result.Snapshot.Phase);
        Assert.True(host.IsRunning);

        host.Stop(TimeSpan.FromSeconds(5));
        Assert.False(host.IsRunning);
    }

    [Fact]
    public async Task StartAsync_CancellationStopsOwnedProcess()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var host = new ServerHost();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        var result = await host.StartAsync(
            CommandShell("ping -n 6 127.0.0.1 >nul"),
            new Regex(@"READY:(\S+)"),
            TimeSpan.FromSeconds(5),
            cancellation.Token);

        Assert.Equal(ServerStartOutcome.Cancelled, result.Outcome);
        Assert.Equal(ServerHostPhase.Stopped, host.Snapshot.Phase);
        Assert.False(host.IsRunning);
    }

    private static ProcessStartInfo CommandShell(string command)
    {
        var shell = Environment.GetEnvironmentVariable("ComSpec")
            ?? throw new InvalidOperationException("ComSpec is unavailable on Windows.");
        var startInfo = new ProcessStartInfo(shell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add(command);
        return startInfo;
    }
}
