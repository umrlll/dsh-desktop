using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace DSHDesktop.Tests;

/// <summary>
/// Locks the WPF/WebView integration seams that the platform-neutral tests cannot execute.
/// These assertions are intentionally structural until an installed WebView smoke lane exists.
/// </summary>
public class StartupContractTests
{
    [Fact]
    public void Launch_RequestsOsAssignedPortAndRecordsAdvertisedPort()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));
        var requestDynamic = source.IndexOf("_port = 0;", StringComparison.Ordinal);
        var addPortFlag = source.IndexOf("psi.ArgumentList.Add(\"--port\")", StringComparison.Ordinal);
        var acceptAdvertised = source.IndexOf("_port = trustedWebOrigin.Port;", StringComparison.Ordinal);
        var rememberOwner = source.IndexOf("RememberServerOwner();", acceptAdvertised, StringComparison.Ordinal);

        Assert.True(requestDynamic >= 0, "启动没有请求 OS 分配端口");
        Assert.True(addPortFlag > requestDynamic, "--port 参数必须使用动态端口请求");
        Assert.True(acceptAdvertised > addPortFlag, "必须从受信任启动 URL 接受实际端口");
        Assert.True(rememberOwner > acceptAdvertised, "只能在知道实际端口后写服务归属记录");
    }

    [Fact]
    public void LastKnownGood_IsCommittedOnlyInsideFrontendHealthGate()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));
        Assert.Single(Regex.Matches(source, @"\bDesktopRecovery\.CommitHealthy\s*\(").Cast<Match>());

        var method = source.IndexOf("private async Task ConfirmFrontendHealthyAsync(", StringComparison.Ordinal);
        Assert.True(method >= 0, "找不到前端健康门");
        var body = SourceScanner.MethodBody(source, method);
        Assert.Contains("FrontendHealthProbe.Result.Healthy", body);
        Assert.Contains("DesktopRecovery.CommitHealthy", body);
    }

    [Fact]
    public void WebViewSecurityEvents_AreAllWiredBeforeStartup()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));
        foreach (var eventName in new[]
                 {
                     "NavigationStarting",
                     "FrameNavigationStarting",
                     "NewWindowRequested",
                     "DownloadStarting",
                     "PermissionRequested",
                 })
        {
            Assert.Contains("Web.CoreWebView2." + eventName + " +=", source);
        }
    }

    [Fact]
    public void MainWindow_DelegatesChildProcessLifecycleToCoreServerHost()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));

        Assert.Contains("readonly IServerHost _serverHost", source);
        Assert.Contains("_serverHost.StartAsync(", source);
        Assert.Contains("_serverHost.Stop(", source);
        Assert.DoesNotContain("Process? _serverProc", source);
        Assert.DoesNotContain("TaskCompletionSource<string> _urlTcs", source);
    }

    [Fact]
    public void MainWindow_DelegatesRuntimeDiscoveryToCoreRuntimeManager()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));

        Assert.Contains("IRuntimeManager Runtime", source);
        Assert.Contains("Runtime.Resolve()", source);
        Assert.Contains("Runtime.FindNpm()", source);
        Assert.DoesNotContain("string? FindNode(", source);
        Assert.DoesNotContain("string? DshNodeBinJs(", source);
        Assert.DoesNotContain("string? LauncherInstallDir(", source);
    }

    [Fact]
    public void MainWindow_DelegatesProfileIdentityAndInitializationToCoreProfileManager()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));

        Assert.Contains("IProfileManager Profiles", source);
        Assert.Contains("Profiles.Initialize(", source);
        Assert.Contains("Profiles.Active.Directory", source);
        Assert.Contains("Profiles.Active.Name", source);
        Assert.DoesNotContain("MarketSupport.ActiveProfile =", source);
        Assert.DoesNotContain("ProfileSeed.Apply", source);
    }

    [Fact]
    public void SafeMode_UsesIsolatedProfileAndShippedTemplateInitialization()
    {
        var mainWindow = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));
        var app = File.ReadAllText(SourceScanner.ProductFile("App.xaml.cs"));
        var xaml = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml"));

        Assert.Contains("--safe-mode", app);
        Assert.Contains("--migrate-profile", app);
        Assert.Contains("ProfileManager.Default.Activate(ProfileMode.Safe)", app);
        Assert.Contains("ProfileManager.Default.MigrateLegacyToDesktop()", app);
        Assert.Contains("psi.ArgumentList.Add(\"--profile\")", mainWindow);
        Assert.Contains("psi.ArgumentList.Add(\"--from-default-profile\")", mainWindow);
        Assert.Contains("profile.InitializationTemplate", mainWindow);
        Assert.Contains("psi.Environment[\"DSH_HOME\"] = profile.DshHome", mainWindow);
        Assert.Contains("Profiles.Activate(nextMode)", mainWindow);
        Assert.Contains("profile.Mode != ProfileMode.Safe", mainWindow);
        Assert.Contains("Click=\"OnMenuToggleSafeMode\"", xaml);
    }

    [Fact]
    public void MainWindow_DelegatesUpdateConcurrencyAndCandidateStateToCoreCoordinator()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));

        Assert.Contains("IUpdateCoordinator _updates", source);
        Assert.Contains("_updates.TryBeginCheck(", source);
        Assert.Contains("_updates.CompleteCheck(", source);
        Assert.Contains("_updates.TryBeginApply(", source);
        Assert.Contains("_updates.CompleteApply(", source);
        Assert.Contains("_updates.FailApply(", source);
        Assert.Contains("_updates.TryDismissCandidate", source);
        Assert.Contains("_releaseSkips.IsSkipped", source);
        Assert.Contains("_releaseSkips.Skip", source);
        Assert.DoesNotContain("bool _checkingForUpdates", source);
        Assert.DoesNotContain("VersionUpdate.Result? _lastUpdate", source);
    }

    [Fact]
    public void UnsignedNpmUpdatePath_IsNotExposedAndSignedReleaseUpdaterOwnsApplication()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));
        var popup = SourceScanner.MethodBody(source, source.IndexOf("private void ShowUpdatePopup()", StringComparison.Ordinal));
        var apply = SourceScanner.MethodBody(source, source.IndexOf("private async Task ApplyUpdateAsync()", StringComparison.Ordinal));

        Assert.Contains("TryGetSignedReleaseSource", popup);
        Assert.Contains("签名更新通道", popup);
        Assert.Contains("signedReleaseRequired", apply);
        Assert.Contains("TryGetSignedReleaseSource", apply);
        Assert.Contains("RunSignedReleaseUpdateAsync", apply);
        Assert.DoesNotContain("RunUpdateAsync(target)", apply);
        Assert.DoesNotContain("RunUpdateAsync", source);
        Assert.DoesNotContain("@deepseek-ai/dsh@", source);
        Assert.True(
            apply.IndexOf("signedReleaseRequired", StringComparison.Ordinal)
            < apply.IndexOf("_updates.TryBeginApply", StringComparison.Ordinal),
            "未配置签名发布源时不得进入 npm staging 更新路径。");

        Assert.Contains("RuntimeReleaseUpdater", source);
        Assert.Contains("RuntimeReleaseFeedClient", source);
        Assert.Contains("DSH_DESKTOP_RELEASE_FEED_CONFIG", source);
        Assert.Contains("DSH_DESKTOP_RELEASE_CHANNEL", source);
        Assert.Contains("result.Acquisition?.Descriptor?.DshVersion", source);
        Assert.Contains("FetchSignedReleaseDescriptorAsync", source);
        Assert.Contains("FetchVerifiedDescriptorAsync", source);
        Assert.Contains("IsSignedCandidateCompatible", source);
        Assert.Contains("RuntimeReleaseCompatibility.Evaluate", source);
        Assert.Contains("AcquireAsync(", source);
        Assert.Contains("ActivateAcquiredCandidate", source);
        Assert.Contains("等待安装确认", source);
        Assert.Contains("暂不安装", source);
        Assert.Contains("DiscardAcquiredCandidate", source);
        Assert.Contains("_releaseUpdateCts", source);
        Assert.Contains("CreateLinkedTokenSource(cancellationToken)", source);
        Assert.Contains("_releaseUpdateCts?.Cancel()", source);
        Assert.Contains("_pendingUpdateRuntimeId", source);
        Assert.Contains("RollBackPendingUpdateForExit()", source);
    }

    [Fact]
    public void UpdateFlow_StagesVerifiedSlotAndRollsBackOnFrontendHealthFailure()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));
        var updateMethod = source.IndexOf("private async Task<RuntimeReleaseUpdateResult> RunSignedReleaseUpdateAsync(", StringComparison.Ordinal);
        Assert.True(updateMethod >= 0, "找不到不可变槽更新入口");
        var body = SourceScanner.MethodBody(source, updateMethod);

        Assert.Contains("RuntimeReleaseFeedClient", body);
        Assert.Contains("RuntimeReleaseUpdater", body);
        Assert.Contains("updater.AcquireAsync", body);
        Assert.Contains("updater.ActivateAcquiredCandidate", body);
        Assert.True(
            body.IndexOf("updater.AcquireAsync", StringComparison.Ordinal)
                < body.IndexOf("updater.ActivateAcquiredCandidate", StringComparison.Ordinal),
            "候选必须先获取和验证，再经用户确认后接纳。");
        Assert.DoesNotContain("@deepseek-ai/dsh@", body);
        var stager = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot, "src", "DSHDesktop.Core", "RuntimeUpdateStager.cs"));
        Assert.Contains("slots.PruneInactiveAndStaging()", stager);
        Assert.DoesNotContain("UpdateBackup.", source);
        Assert.Contains("WaitForFrontendHealthAsync", source);
        Assert.Contains("new RuntimeSlotManager(Runtime.WritableRuntimeRoot)", source);
        Assert.Contains("var rollback = slots.Rollback()", source);
        Assert.Contains("slots.QuarantineInactive(result.Adoption.RuntimeId)", source);
    }

    [Fact]
    public void MainWindow_DelegatesAutomaticRestartPolicyToCoreRecoveryCoordinator()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));

        Assert.Contains("IRecoveryCoordinator _recovery", source);
        Assert.Contains("_recovery.HandleUnexpectedExit(", source);
        Assert.Contains("_recovery.EnterAssistance()", source);
        Assert.Contains("_recovery.PrepareManualRetry()", source);
        Assert.Contains("_recovery.RecordHealthy()", source);
        Assert.DoesNotContain("int _autoRestarts", source);
        Assert.DoesNotContain("MaxAutoRestarts", source);
    }

    [Fact]
    public void RuntimeManagement_RequiresConfirmationAndRestoresTheOriginalSlotAfterHealthFailure()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));
        var xaml = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml"));
        var method = source.IndexOf("private async void OnMenuManageRuntime(", StringComparison.Ordinal);
        Assert.True(method >= 0, "找不到运行时管理入口");
        var body = SourceScanner.MethodBody(source, method);

        Assert.Contains("运行时信息与回退", xaml);
        Assert.Contains("RuntimeSlotManager", body);
        Assert.Contains("回退并重启", body);
        Assert.Contains("cancel: \"取消\"", body);
        Assert.Contains("slots.Rollback()", body);
        Assert.Contains("WaitForFrontendHealthAsync", body);
        Assert.Contains("restoredHealthy", body);
        Assert.Contains("Runtime.Invalidate()", body);
    }

    [Fact]
    public void DiagnosticExport_OffersAWindowsNativeDestinationPicker()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));
        var method = source.IndexOf("private async void OnExportDiagnostics(", StringComparison.Ordinal);
        Assert.True(method >= 0, "找不到诊断导出入口");
        var body = SourceScanner.MethodBody(source, method);

        Assert.Contains("FolderBrowserDialog", body);
        Assert.Contains("选择目录", body);
        Assert.Contains("导出到日志目录", body);
        Assert.Contains("picker.SelectedPath", body);
    }

    [Fact]
    public void DesktopProject_DeclaresPerMonitorV2DpiAwareness()
    {
        var project = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot, "src", "DSHDesktop", "DSHDesktop.csproj"));
        var manifest = File.ReadAllText(Path.Combine(
            SourceScanner.RepoRoot, "src", "DSHDesktop", "app.manifest"));

        Assert.Contains("<ApplicationManifest>app.manifest</ApplicationManifest>", project);
        Assert.Contains("<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>", project);
        Assert.Contains("asInvoker", manifest);
    }

    [Fact]
    public void RecoveryActions_RequireExplicitConfirmationBeforeChangingTheProfile()
    {
        var source = File.ReadAllText(SourceScanner.ProductFile("MainWindow.xaml.cs"));
        var method = source.IndexOf("private async Task ShowRecoveryAssistantAsync(", StringComparison.Ordinal);
        Assert.True(method >= 0, "找不到恢复助手入口");
        var body = SourceScanner.MethodBody(source, method);

        Assert.Contains("ConfirmRecoveryMutation(", body);
        Assert.Contains("禁用可疑插件？", body);
        Assert.Contains("恢复被禁用的插件？", body);
        Assert.Contains("回滚到可用状态？", body);
        Assert.Contains("DesktopRecovery.DisableBundles", body);
        Assert.Contains("DesktopRecovery.ReEnableBundles", body);
        Assert.Contains("DesktopRecovery.Rollback", body);
        Assert.Contains("cancel: \"取消\"", source);
        Assert.Contains("AnalyzeSuspectBundles", body);
        Assert.Contains("DescribeSuspects", body);
    }
}
