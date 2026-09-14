using System;
using System.Collections.Generic;
using System.IO;

namespace DSHDesktop;

/// <summary>
/// 更新前备份与失败回滚（优化清单 B5 的「方案 C」）。
///
/// 设计边界（与方案 C 的约定一致）：
/// - **不改公开契约**：更新仍是就地 <c>npm install @deepseek-ai/dsh@&lt;ver&gt;</c>，安装目录布局不变；
///   本类只在更新前把「明确列举的关键资产」复制到安装目录内的一个固定备份根，失败时还原。
/// - **只作用于列举资产**（t14 扩充后共 4 项）：<c>package.json</c>、<c>package-lock.json</c>、
///   <c>node_modules/.package-lock.json</c>、<c>node_modules/@deepseek-ai/dsh</c>（递归）。
///   不触碰 profile 目录、runtime 目录或任何用户数据。
/// - **传递依赖树不覆盖（t14 登记）**：<c>node_modules</c> 下 **dsh CLI 包之外的包**不在备份范围内
///   （整棵 node_modules 约 216.9 MB，是否纳入属产品决策）。后果：回滚后可能出现
///   「dsh CLI 包与锁文件已回到更新前，但它依赖的其它包仍是更新后版本」的混合状态；
///   <c>npm ci</c> 会依据 <c>package-lock.json</c> 重建整棵依赖树，因此需要完全一致时应
///   重跑一次更新（或按锁文件执行 <c>npm ci</c>）。此边界必须在用户可见文案中标明。
/// - **同一时刻至多一份备份**：备份根名固定，创建前先清理上一轮遗留目录。
/// </summary>
internal static class UpdateBackup
{
    /// <summary>备份根目录名；固定名称即「同一时刻至多一份备份」。</summary>
    internal const string BackupDirName = ".dsh-desktop-update-backup";

    /// <summary>被备份/还原的资产，均为相对 <c>LauncherInstallDir()</c> 的明确列举项。</summary>
    private static readonly string[] Assets =
    {
        "package.json",
        "package-lock.json",
        Path.Combine("node_modules", ".package-lock.json"),
        Path.Combine("node_modules", "@deepseek-ai", "dsh"),
    };

    /// <summary>备份根目录的完整路径（命名规则：<c>&lt;LauncherInstallDir()&gt;/.dsh-desktop-update-backup</c>）。</summary>
    internal static string Root(string installDir) => Path.Combine(installDir, BackupDirName);

    /// <summary>
    /// 建立更新前备份：先清理上一轮遗留的备份目录，再复制列举资产。
    /// **fail-safe**：返回 <c>Success == false</c> 时调用方必须中止更新并给出可见反馈，不得静默继续。
    /// </summary>
    internal static UpdateBackupResult Create(string installDir)
    {
        var root = Root(installDir);
        try
        {
            if (Directory.Exists(root))
            {
                try { Directory.Delete(root, recursive: true); }
                catch (Exception ex)
                {
                    return UpdateBackupResult.Fail(
                        "清理上一轮更新备份失败（" + root + "）: " + DesktopLog.Describe(ex), root);
                }
            }
            Directory.CreateDirectory(root);

            var missing = new List<string>();
            foreach (var asset in Assets)
            {
                var src = Path.Combine(installDir, asset);
                var dst = Path.Combine(root, asset);
                if (!IsUnder(installDir, src))
                    return UpdateBackupResult.Fail("备份资产路径越界，已中止: " + src, root);

                if (File.Exists(src))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    File.Copy(src, dst, overwrite: true);
                }
                else if (Directory.Exists(src))
                {
                    CopyDirectory(src, dst);
                }
                else
                {
                    missing.Add(asset);
                }
            }

            // package.json 是「更新前状态」的核心资产：缺它就不算建立了可用备份。
            if (missing.Contains("package.json"))
                return UpdateBackupResult.Fail("安装目录缺少 package.json，未能建立可回滚的备份。", root);

            return UpdateBackupResult.Ok(root, missing);
        }
        catch (Exception ex)
        {
            return UpdateBackupResult.Fail("建立更新备份失败: " + DesktopLog.Describe(ex), root);
        }
    }

    /// <summary>
    /// 把备份内容还原回原位。逐资产有三种行为：
    /// ① 备份里有文件 ⇒ 覆盖目标；② 备份里有目录 ⇒ **先拷到同目录临时名，再删除旧目录并切换**
    /// （t14/F2，避免中途失败留下空洞，失败时清理临时目录并把其路径写进失败信息）；
    /// ③ 备份里没有、但目标当前存在 ⇒ **删除目标**，复位为「更新前本就不存在」（t14/F1，
    /// 覆盖「本次更新让 npm 新建了锁文件」这类残留）。
    /// 结果区分「已回滚成功」与「回滚失败，需人工介入」；调用方必须把两种结果分别写进日志与状态栏
    /// （不得静默吞掉），且必须如实说明回滚范围（传递依赖树不在备份内，见类文档边界）。
    /// </summary>
    internal static UpdateBackupResult Restore(string installDir)
    {
        var root = Root(installDir);
        try
        {
            if (!Directory.Exists(root))
                return UpdateBackupResult.Fail("没有可用的更新备份（" + root + " 不存在）。", root);

            var failed = new List<string>();
            foreach (var asset in Assets)
            {
                var bak = Path.Combine(root, asset);
                var dst = Path.Combine(installDir, asset);
                if (!IsUnder(installDir, dst))
                {
                    failed.Add(asset + "（路径越界）");
                    continue;
                }
                try
                {
                    if (File.Exists(bak))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                        File.Copy(bak, dst, overwrite: true);
                    }
                    else if (Directory.Exists(bak))
                    {
                        // 先拷到同目录临时名，全部成功后再切换（t14/F2）：避免「删了旧目录、拷贝中途失败」
                        // 在原位留下空洞。临时目录名带时间戳，失败时清理并把路径写进失败信息。
                        var tmp = dst + ".restore-tmp-" + DateTime.Now.ToString("yyyyMMddHHmmssfff");
                        try
                        {
                            if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true);
                            CopyDirectory(bak, tmp);
                            if (Directory.Exists(dst)) Directory.Delete(dst, recursive: true);
                            Directory.Move(tmp, dst);
                        }
                        catch (Exception switchEx)
                        {
                            try { if (Directory.Exists(tmp)) Directory.Delete(tmp, recursive: true); }
                            catch { /* 清理失败不影响下面的失败上报 */ }
                            throw new IOException(
                                "切换还原目录失败（临时目录 " + tmp + "）: " + DesktopLog.Describe(switchEx), switchEx);
                        }
                    }
                    else if (File.Exists(dst) || Directory.Exists(dst))
                    {
                        // 第三种行为（t14/F1）：备份中不存在该资产、但目标当前存在 ⇒ 删除目标，
                        // 复位为「更新前本就不存在」。典型情形：本次更新让 npm 新建了锁文件，
                        // 而更新前的备份里没有它——若不动它，失败回滚后会留下不属于更新前状态的残留。
                        if (File.Exists(dst)) File.Delete(dst);
                        else Directory.Delete(dst, recursive: true);
                    }
                    // 备份中没有、目标当前也没有 ⇒ 无需动作。
                }
                catch (Exception ex)
                {
                    failed.Add(asset + "（" + DesktopLog.Describe(ex) + "）");
                }
            }

            return failed.Count == 0
                ? UpdateBackupResult.Ok(root, Array.Empty<string>())
                : UpdateBackupResult.Fail("还原失败：" + string.Join("；", failed), root);
        }
        catch (Exception ex)
        {
            return UpdateBackupResult.Fail("还原更新备份失败: " + DesktopLog.Describe(ex), root);
        }
    }

    /// <summary>删除备份目录（成功路径专用；失败不影响更新结果）。</summary>
    internal static void Discard(string installDir)
    {
        var root = Root(installDir);
        try
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
        catch (Exception ex)
        {
            DesktopLog.Warn("清理更新备份失败（不影响更新结果）: " + DesktopLog.Describe(ex));
        }
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }

    /// <summary>只允许作用在安装目录内部的路径（防止越界触碰 profile/runtime/用户数据）。</summary>
    private static bool IsUnder(string root, string path)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path);
        return full.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>备份/还原结果。成功时 <see cref="Message"/> 为空；失败时 <see cref="Message"/> 必须原样呈现给用户。</summary>
internal sealed class UpdateBackupResult
{
    internal bool Success { get; private init; }

    internal string Message { get; private init; } = "";

    internal string Root { get; private init; } = "";

    /// <summary>更新前本就不存在、因此未参与备份的资产（须记录，不得静默）。</summary>
    internal IReadOnlyList<string> Missing { get; private init; } = Array.Empty<string>();

    internal static UpdateBackupResult Ok(string root, IReadOnlyList<string> missing)
        => new() { Success = true, Root = root, Missing = missing };

    internal static UpdateBackupResult Fail(string message, string root)
        => new() { Success = false, Message = message, Root = root };
}
