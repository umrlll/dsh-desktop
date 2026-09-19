using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using DSHDesktop.Core;

namespace DSHDesktop;

/// <summary>恢复助手给出的选择。</summary>
internal enum RecoveryAction
{
    None,
    OpenLogs,
    DisableLastPlugin,
    ReEnable,
    Rollback,
    Retry,
}

/// <summary>
/// profile 启动状态的所有权：启动前拍快照、成功启动后提交"已知可用"、失败时提供
/// 可回滚与"禁用最后安装的插件"。
///
/// 存在理由：一个坏插件会让整个 profile 起不来（dsh 的 loader 一旦有 entry 加载失败就
/// 中止整次启动），而此前唯一的补救手段是手写 PowerShell 去改 package.json。这里把它
/// 变成程序内建、有备份、可逆转的能力。
///
/// 所有写入都遵守：先备份 → 再改 → 失败回滚；禁用项记进 sidecar，可一键恢复。
/// </summary>
internal static class DesktopRecovery
{
    /// <summary>sidecar 文件名，记录被本程序禁用的 bundle，用于恢复。</summary>
    private const string DisabledRecordName = "dsh-desktop-disabled.json";

    /// <summary>快照里保留的 profile 清单文件（都是决定"能不能起来"的那些）。</summary>
    private static readonly string[] ManifestFiles =
    {
        "package.json",
        "cordis.patch.yml",
        "pnpm-lock.yaml",
        "pnpm-workspace.yaml",
    };

    /// <summary>
    /// 桌面端状态目录（快照 / 禁用记录）。默认 <c>%LOCALAPPDATA%\DSHDesktop\profile-state</c>；
    /// 可用 <c>DSH_DESKTOP_STATE_ROOT</c> 覆盖（便携部署与自动化验证用）。
    /// </summary>
    public static string StateRoot
    {
        get
        {
            var overridden = Environment.GetEnvironmentVariable("DSH_DESKTOP_STATE_ROOT");
            if (!string.IsNullOrWhiteSpace(overridden)) return overridden;
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
            return Path.Combine(root, "DSHDesktop", "profile-state");
        }
    }

    private static string ProfileStateDir(string profile) => Path.Combine(StateRoot, Sanitize(profile));
    private static string SnapshotDir(string profile) => Path.Combine(ProfileStateDir(profile), "last-known-good");

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }

    // ---------- 快照 ----------

    /// <summary>
    /// 在启动之前拍一份清单快照。只在下一次成功启动后才把它提交为"已知可用"，
    /// 因此这份快照始终代表"上一次确实起来过的配置"。
    /// </summary>
    public static void SnapshotBeforeStart(string profileDir, string profile)
    {
        try
        {
            var pending = Path.Combine(ProfileStateDir(profile), "pending");
            if (Directory.Exists(pending)) Directory.Delete(pending, recursive: true);
            Directory.CreateDirectory(pending);
            foreach (var f in ManifestFiles)
            {
                var src = Path.Combine(profileDir, f);
                if (File.Exists(src)) File.Copy(src, Path.Combine(pending, f), overwrite: true);
            }
            File.WriteAllText(Path.Combine(pending, "meta.json"), JsonSerializer.Serialize(new
            {
                profile,
                capturedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                bundles = ReadBundles(profileDir),
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            DesktopLog.Error("拍启动快照失败", ex);
        }
    }

    /// <summary>启动确实就绪后调用：把 pending 提升为 last-known-good。</summary>
    public static void CommitHealthy(string profile, int port)
    {
        try
        {
            var pending = Path.Combine(ProfileStateDir(profile), "pending");
            if (!Directory.Exists(pending)) return;
            var good = SnapshotDir(profile);
            // good 由 ProfileStateDir(profile) 与固定子目录名组合而成，必然含父目录
            Directory.CreateDirectory(Path.GetDirectoryName(good)!);

            // 提交换入必须「先保留旧值 → 再换入 → 成功后才清理」（优化清单 B2）。
            // 旧实现先 Delete(good) 再 Move(pending, good)：两步之间失败会同时丢掉旧快照与
            // pending，回滚能力被永久移除，而用户只看到"还没有可回滚的可用快照"。
            string? previous = null;
            if (Directory.Exists(good))
            {
                previous = good + ".old-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                Directory.Move(good, previous);
            }

            try
            {
                Directory.Move(pending, good);
            }
            catch
            {
                // 换入失败：把旧快照移回原位，保持"失败即维持原状"。
                if (previous != null && !Directory.Exists(good))
                {
                    try { Directory.Move(previous, good); }
                    catch (Exception restoreEx)
                    {
                        DesktopLog.Warn("可用状态换入失败后回滚旧快照也失败: " + DesktopLog.Describe(restoreEx));
                    }
                }
                throw;
            }

            // 换入已成功：无条件清理中间/遗留快照（含上次提交失败留下的 *.old-*），
            // 避免 previous == null 时把遗留目录一直留在磁盘上。
            try
            {
                // good 由 ProfileStateDir(profile) 与固定子目录名组合而成，必然含父目录
                var stateDir = Path.GetDirectoryName(good)!;
                foreach (var stale in Directory.GetDirectories(stateDir, Path.GetFileName(good) + ".old-*"))
                {
                    try { Directory.Delete(stale, recursive: true); }
                    catch (Exception ex)
                    {
                        DesktopLog.Warn("清理旧可用状态快照失败（不影响本次提交）: " + DesktopLog.Describe(ex));
                    }
                }
            }
            catch (Exception ex)
            {
                DesktopLog.Warn("扫描遗留的旧可用状态快照失败（不影响本次提交）: " + DesktopLog.Describe(ex));
            }

            File.AppendAllText(Path.Combine(good, "healthy.txt"),
                DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  port=" + port + Environment.NewLine);
            DesktopLog.Info($"已提交可用启动状态（profile={profile} port={port}）");
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("提交可用启动状态失败: " + DesktopLog.Describe(ex));
        }
    }

    /// <summary>最近一次"已知可用"快照的时间；没有则为 null。</summary>
    public static string? LastGoodSummary(string profile)
    {
        try
        {
            var meta = Path.Combine(SnapshotDir(profile), "meta.json");
            if (!File.Exists(meta)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(meta));
            var at = doc.RootElement.TryGetProperty("capturedAt", out var v) ? v.GetString() : null;
            var healthy = Path.Combine(SnapshotDir(profile), "healthy.txt");
            var ok = File.Exists(healthy) ? File.ReadLines(healthy).LastOrDefault() : null;
            return "快照时间 " + (at ?? "?") + (ok == null ? "（未验证）" : "，验证于 " + ok.Trim());
        }
        catch { return null; }
    }

    /// <summary>回滚到最近一次已知可用快照（当前清单先另存为 .bak-时间戳）。</summary>
    public static bool Rollback(string profileDir, string profile, out string message)
    {
        try
        {
            var good = SnapshotDir(profile);
            if (!Directory.Exists(good))
            {
                message = "还没有已提交的可用快照，无法回滚。至少需要成功启动过一次。";
                return false;
            }
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var restored = new List<string>();
            foreach (var f in ManifestFiles)
            {
                var src = Path.Combine(good, f);
                if (!File.Exists(src)) continue;
                var dst = Path.Combine(profileDir, f);
                if (File.Exists(dst)) File.Copy(dst, dst + ".bak-" + stamp, overwrite: true);
                File.Copy(src, dst, overwrite: true);
                restored.Add(f);
            }
            // 回滚会把被禁用的记录一并作废：清单已经回到当时的样子
            var record = Path.Combine(profileDir, DisabledRecordName);
            if (File.Exists(record)) File.Delete(record);
            PruneGeneratedBackups(profileDir);
            message = "已回滚 " + restored.Count + " 个清单文件（原文件已备份为 *.bak-" + stamp + "）：" + string.Join(", ", restored);
            DesktopLog.Info("回滚 profile: " + message);
            return true;
        }
        catch (Exception ex)
        {
            message = "回滚失败: " + ex.Message;
            DesktopLog.Error("回滚失败", ex);
            return false;
        }
    }

    // ---------- 禁用最后安装的插件 ----------

    private static List<string> ReadBundles(string profileDir)
    {
        try
        {
            var path = Path.Combine(profileDir, "package.json");
            if (!File.Exists(path)) return new List<string>();
            var node = JsonNode.Parse(File.ReadAllText(path));
            if (node?["dsh"]?["profile"]?["bundles"] is not JsonArray arr) return new List<string>();
            return arr.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
        }
        catch { return new List<string>(); }
    }

    /// <summary>
    /// 找出"快照之后新加进 bundles 的项"，即最可能是罪魁的那个插件。
    /// 没有快照时退化为"bundles 里的最后一项"。
    /// </summary>
    public static List<string> FindSuspectBundles(string profileDir, string profile)
    {
        var current = ReadBundles(profileDir);
        var baseline = new List<string>();
        var meta = Path.Combine(SnapshotDir(profile), "meta.json");
        try
        {
            if (File.Exists(meta))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(meta));
                if (doc.RootElement.TryGetProperty("bundles", out var b) && b.ValueKind == JsonValueKind.Array)
                    baseline = b.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList();
            }
        }
        catch { }

        // 内置基线永远不动
        var protectedNames = new[] { "@deepseek-ai/dsh-base", "@deepseek-ai/dsh-web-app" };
        var added = current.Where(x => !baseline.Contains(x) && !protectedNames.Contains(x)).ToList();
        if (added.Count == 0 && current.Count > 0)
        {
            var last = current[^1];
            if (!protectedNames.Contains(last)) added.Add(last);
        }
        return added;
    }

    /// <summary>
    /// 从 <c>dsh.profile.bundles</c> 里移除指定项，并把移除记录写进 sidecar 以便恢复。
    /// 只动 bundles（不改 dependencies），因此 pnpm 层不受影响，改回来就是一行的事。
    /// </summary>
    public static bool DisableBundles(string profileDir, string profile, IEnumerable<string> bundles, out string message)
    {
        var targets = bundles.Distinct().ToList();
        if (targets.Count == 0) { message = "没有可禁用的插件。"; return false; }
        try
        {
            var path = Path.Combine(profileDir, "package.json");
            if (!File.Exists(path)) { message = "profile package.json 不存在。"; return false; }

            var original = File.ReadAllText(path);
            var backup = path + ".bak-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            File.Copy(path, backup, overwrite: true);

            var node = JsonNode.Parse(original) ?? throw new InvalidOperationException("package.json 解析失败");
            if (node["dsh"]?["profile"]?["bundles"] is not JsonArray arr)
            {
                message = "profile package.json 里没有 dsh.profile.bundles。";
                return false;
            }
            var kept = arr.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0 && !targets.Contains(s)).ToList();
            node["dsh"]!["profile"]!["bundles"] = new JsonArray(kept.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());

            var serialized = node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine;
            File.WriteAllText(path, serialized);

            // sidecar 记录，供"恢复"使用
            var recordPath = Path.Combine(profileDir, DisabledRecordName);
            var record = new JsonObject
            {
                ["disabledAt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
                ["profile"] = profile,
                ["backup"] = Path.GetFileName(backup),
                ["bundles"] = new JsonArray(targets.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray()),
            };
            File.WriteAllText(recordPath, record.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            PruneGeneratedBackups(profileDir);

            message = "已从 bundles 禁用 " + targets.Count + " 个插件：" + string.Join(", ", targets)
                      + "\n清单已备份为 " + Path.GetFileName(backup) + "，可通过「恢复被禁用的插件」还原。";
            DesktopLog.Warn("禁用 bundle: " + string.Join(", ", targets));
            return true;
        }
        catch (Exception ex)
        {
            message = "禁用失败: " + ex.Message;
            DesktopLog.Error("禁用 bundle 失败", ex);
            return false;
        }
    }

    /// <summary>恢复先前被本程序禁用的 bundle。</summary>
    public static bool ReEnableBundles(string profileDir, out string message)
    {
        try
        {
            var recordPath = Path.Combine(profileDir, DisabledRecordName);
            if (!File.Exists(recordPath)) { message = "没有被本程序禁用的插件记录。"; return false; }
            using var doc = JsonDocument.Parse(File.ReadAllText(recordPath));
            var targets = doc.RootElement.TryGetProperty("bundles", out var b) && b.ValueKind == JsonValueKind.Array
                ? b.EnumerateArray().Select(x => x.GetString() ?? "").Where(s => s.Length > 0).ToList()
                : new List<string>();
            if (targets.Count == 0) { message = "记录为空。"; return false; }

            var path = Path.Combine(profileDir, "package.json");
            var node = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidOperationException("package.json 解析失败");
            if (node["dsh"]?["profile"]?["bundles"] is not JsonArray arr)
            {
                message = "profile package.json 里没有 dsh.profile.bundles。";
                return false;
            }
            var current = arr.Select(x => x?.GetValue<string>() ?? "").Where(s => s.Length > 0).ToList();
            foreach (var t in targets) if (!current.Contains(t)) current.Add(t);
            node["dsh"]!["profile"]!["bundles"] = new JsonArray(current.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
            File.Delete(recordPath);
            message = "已恢复 " + targets.Count + " 个插件：" + string.Join(", ", targets);
            DesktopLog.Info("恢复 bundle: " + string.Join(", ", targets));
            return true;
        }
        catch (Exception ex)
        {
            message = "恢复失败: " + ex.Message;
            DesktopLog.Error("恢复 bundle 失败", ex);
            return false;
        }
    }

    private static void PruneGeneratedBackups(string profileDir)
    {
        try
        {
            var result = ProfileBackupRetention.Prune(profileDir, ManifestFiles);
            if (!result.Success)
                DesktopLog.Warn("Profile backup maintenance did not finish: " + string.Join(", ", result.Errors.Take(3)));
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("Profile backup maintenance failed: " + DesktopLog.Describe(ex));
        }
    }

    public static bool HasDisabledRecord(string profileDir) => File.Exists(Path.Combine(profileDir, DisabledRecordName));
}

/// <summary>
/// 原生恢复助手。连续启动失败后弹出，给出四条出路而不是只留一行文字。
/// 用代码构建界面（不新增 XAML），避免动到主窗口的布局。
/// </summary>
internal sealed class RecoveryDialog
{
    private readonly Window _window;
    private RecoveryAction _result = RecoveryAction.None;

    public RecoveryDialog(string headline, string detail, bool canDisable, bool canRollback, bool canReEnable)
    {
        _window = new Window
        {
            Title = "DSH Desktop — 启动失败",
            Width = 560,
            SizeToContent = SizeToContent.Height,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x16, 0x1A, 0x1E)),
        };

        var panel = new StackPanel { Margin = new Thickness(18) };

        panel.Children.Add(new TextBlock
        {
            Text = headline,
            Foreground = System.Windows.Media.Brushes.White,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xA3, 0xB2)),
            FontSize = 12,
            Margin = new Thickness(0, 8, 0, 14),
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 220,
        });

        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        void Add(string label, RecoveryAction action, bool primary = false, bool ghost = false)
        {
            // 按钮外观统一走 AppDialog 的三档工厂（品牌实心 / 描边 / 幽灵），
            // 与自绘对话框、浮层保持同一套观感；先前这里是系统默认按钮。
            void Click() { _result = action; _window.Close(); }
            var button = ghost
                ? AppDialog.GhostButton(label, Click)
                : primary ? AppDialog.PrimaryButton(label, Click) : AppDialog.SecondaryButton(label, Click);
            button.Margin = new Thickness(8, 0, 0, 0);
            button.MinWidth = 96;
            buttons.Children.Add(button);
        }

        Add("查看日志", RecoveryAction.OpenLogs, ghost: true);
        if (canReEnable) Add("恢复被禁用的插件", RecoveryAction.ReEnable);
        if (canDisable) Add("禁用最后安装的插件", RecoveryAction.DisableLastPlugin, primary: true);
        if (canRollback) Add("回滚到可用状态", RecoveryAction.Rollback);
        Add("重试", RecoveryAction.Retry, primary: !canDisable);

        panel.Children.Add(buttons);
        _window.Content = panel;
    }

    /// <summary>显示为模态；返回用户选择（关闭窗口视为不处理）。</summary>
    public RecoveryAction ShowDialog(Window? owner)
    {
        if (owner != null && owner.IsVisible) _window.Owner = owner;
        _window.ShowDialog();
        return _result;
    }
}
