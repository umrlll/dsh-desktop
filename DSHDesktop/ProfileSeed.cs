using System;
using System.IO;
using System.Text;

namespace DSHDesktop;

/// <summary>
/// 发布时可选内置的 profile 种子。
///
/// 打包机上装好的插件（profile 清单 + 扁平 node_modules）随应用一起发布到
/// <c>runtime\profile-seed\</c>；在目标机器首次启动、且该 profile <b>还不存在</b>时
/// 铺开，于是收件人开箱就带着这些插件，不必自己装。
///
/// 两条边界：
/// <list type="bullet">
/// <item>只在目标 profile 不存在时铺开——已存在的 profile 是用户数据，绝不覆盖、不合并。</item>
/// <item>种子里的 node_modules 是扁平布局（profile 用 <c>nodeLinker: hoisted</c>），
///   发布时已剔除 pnpm 的本机元数据（<c>.modules.yaml</c> 等记录着打包机的 store 绝对路径），
///   否则目标机上第一次装插件会被 store 不一致挡住。</item>
/// </list>
///
/// 用 <c>DSHDESKTOP_NO_PROFILE_SEED=1</c> 可以跳过铺开（想要干净 profile 时用）。
/// </summary>
internal static class ProfileSeed
{
    /// <summary>种子目录名，位于应用的 <c>runtime\</c> 之下。</summary>
    public const string SeedFolderName = "profile-seed";

    /// <summary>发布目录里的种子路径。</summary>
    public static string DefaultSeedRoot()
        => Path.Combine(AppContext.BaseDirectory, "runtime", SeedFolderName);

    /// <summary>一次铺开的结果，供状态栏提示。</summary>
    public readonly record struct Result(bool Seeded, string? Notice);

    /// <summary>把种子铺到 <c>&lt;dshHome&gt;\profiles\&lt;profile&gt;</c>。</summary>
    public static Result Apply(string dshHome, string profile, string? seedRoot = null)
        => ApplyTo(Path.Combine(dshHome, "profiles", profile), seedRoot ?? DefaultSeedRoot());

    /// <summary>
    /// 把 <paramref name="seedRoot"/> 铺到 <paramref name="profileDir"/>。
    /// 没有种子、或目标 profile 已存在时什么都不做。
    /// </summary>
    internal static Result ApplyTo(string profileDir, string seedRoot)
    {
        try
        {
            if (Environment.GetEnvironmentVariable("DSHDESKTOP_NO_PROFILE_SEED") == "1")
                return new Result(false, null);
            if (!File.Exists(Path.Combine(seedRoot, "package.json")))
                return new Result(false, null);
            if (File.Exists(Path.Combine(profileDir, "package.json")))
                return new Result(false, null);   // 用户已有 profile：不覆盖

            Directory.CreateDirectory(profileDir);
            CopyTree(seedRoot, profileDir);
            return new Result(true, $"已铺开随应用内置的插件（{profileDir}）。");
        }
        catch (Exception ex)
        {
            // 铺不开只是少了个便利，不能让应用起不来；失败时保持"没有 profile"的原样，
            // dsh 会按默认模板初始化。
            return new Result(false, "内置插件集成失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 铺开时要跳过的名字：pnpm 的内部目录与本机 store 元数据。
    ///
    /// 发布时已经用 robocopy 剔除过一遍，这里再拦一次是有意的——留着它们不只是浪费空间：
    /// <c>.modules.yaml</c> 里是打包机的 store 绝对路径，目标机上第一次装插件会被
    /// <c>ERR_PNPM_UNEXPECTED_STORE</c> 挡住；<c>.pnpm</c> 在非 hoisted 布局下是符号链接农场，
    /// <c>.bin</c> 里的垫片写死了打包机的 node 路径。这些都由 pnpm 在下次安装时重建。
    /// </summary>
    private static readonly string[] SkippedNames =
    {
        ".pnpm", ".bin",
        ".modules.yaml", ".package-map.json", ".pnpm-workspace-state-v1.json",
    };

    private static bool IsSkipped(string name)
    {
        foreach (var skipped in SkippedNames)
            if (string.Equals(skipped, name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var name = Path.GetFileName(file);
            if (IsSkipped(name)) continue;
            File.Copy(file, Path.Combine(target, name), overwrite: true);
        }
        foreach (var dir in Directory.GetDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (IsSkipped(name)) continue;
            CopyTree(dir, Path.Combine(target, name));
        }
    }
}
