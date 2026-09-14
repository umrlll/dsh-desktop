using System;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DSHDesktop;

/// <summary>本窗口托管的 dsh 子进程的"运行时归属"记录。</summary>
internal sealed record ServerRecord(int Pid, int Port, DateTimeOffset StartedAt);

/// <summary>某个端口上的占用者是谁。</summary>
internal enum PortOwner
{
    /// <summary>没人监听。</summary>
    Free,

    /// <summary>本窗口上一次启动的 dsh 服务仍在跑（强杀 / 崩溃留下的孤儿）。</summary>
    OwnLeftover,

    /// <summary>别的程序在占用——绝不能动它，照旧顺延端口。</summary>
    Foreign,
}

/// <summary>某个 pid 的存活证据。取不到的字段一律 false（宁可当作"不是我们的"）。</summary>
internal readonly record struct ProcessEvidence(bool Exists, bool IsNode, bool StartTimeMatches);

/// <summary>
/// 记录"上一次本窗口在哪个端口、起了哪个 pid 的 dsh 服务"，用于启动时防止**同时运行两个服务**。
///
/// 背景：`dsh web` 是桌面端的子进程。强杀桌面端（部署脚本、任务管理器、崩溃）时它会变成
/// 孤儿，而端口仍被占着；旧逻辑对"端口被占"一律顺延（`_port++`），于是孤儿还在跑、这边又起
/// 一个——用户看到的就是"多运行服务"（2026-09-12 反馈）。
///
/// 有了这份记录才敢区分两种"端口被占"：**自己上次的遗留**（可以提示接管）与**别人的进程**
/// （只能顺延）。判定要求三条证据同时成立：记录里的端口一致、pid 仍存活、且它是 node.exe、
/// 且进程启动时刻与记录吻合（挡住 PID 复用）。任何一条取不到 → 按 Foreign 处理，不做任何
/// 破坏性动作。
///
/// 本类刻意不依赖 WPF / DesktopRecovery：状态根由调用方传入（`DesktopRecovery.StateRoot`），
/// 于是它能被一个普通 net10.0 控制台装具真跑（见 D:\dsh\.build\port-owner-harness）。
/// </summary>
internal static class ServerState
{
    private const string FileName = "server.json";

    /// <summary>状态文件路径：<c>&lt;root&gt;/&lt;profile&gt;/server.json</c>。</summary>
    public static string PathFor(string root, string profile)
        => Path.Combine(root, Sanitize(profile), FileName);

    /// <summary>写入归属记录（临时文件 + 覆盖改名，避免读到写一半的 JSON）。失败只影响本特性。</summary>
    public static void Write(string root, string profile, ServerRecord record)
    {
        var path = PathFor(root, profile);
        var tmp = path + ".tmp";
        // path 由 Path.Combine(root, Sanitize(profile), FileName) 构造，必然含父目录
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(tmp, JsonSerializer.Serialize(record));
        File.Move(tmp, path, overwrite: true);
    }

    /// <summary>读归属记录。文件不存在或读不懂都返回 null（按"没有遗留"处理）。</summary>
    public static ServerRecord? Read(string root, string profile)
    {
        try
        {
            var path = PathFor(root, profile);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ServerRecord>(File.ReadAllText(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>清掉归属记录（我们主动停掉了服务，或已接管遗留服务）。</summary>
    public static void Clear(string root, string profile)
    {
        try
        {
            var path = PathFor(root, profile);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // 清不掉不影响正确性：pid 存活 + 启动时刻两道校验自然会挡住误判。
        }
    }

    /// <summary>
    /// 纯决策表（可被装具真跑）：
    /// 端口空着 → Free；记录与本机证据全部吻合 → OwnLeftover；其余 → Foreign。
    /// </summary>
    public static PortOwner Classify(int port, bool portInUse, ServerRecord? record, ProcessEvidence evidence)
    {
        if (!portInUse) return PortOwner.Free;
        if (record is { } r
            && r.Port == port
            && evidence.Exists
            && evidence.IsNode
            && evidence.StartTimeMatches)
        {
            return PortOwner.OwnLeftover;
        }
        return PortOwner.Foreign;
    }

    private static string Sanitize(string name)
    {
        var chars = name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray();
        return new string(chars);
    }
}
