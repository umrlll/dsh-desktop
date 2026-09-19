using System.Text.Json;

namespace DSHDesktop.Core;

/// <summary>本窗口托管的 DSH 子进程的运行时归属记录。</summary>
public sealed record ServerRecord(int Pid, int Port, DateTimeOffset StartedAt);

/// <summary>某个端口上的占用者分类。</summary>
public enum PortOwner
{
    Free,
    OwnLeftover,
    Foreign,
}

/// <summary>某个进程的存活证据；无法取得的证据应保持为 false。</summary>
public readonly record struct ProcessEvidence(bool Exists, bool IsNode, bool StartTimeMatches);

/// <summary>
/// 持久化上一次由桌面端启动的 DSH 服务，并以保守证据识别遗留进程。
/// 本类型不依赖 WPF、WinForms 或 WebView2。
/// </summary>
public static class ServerState
{
    private const string FileName = "server.json";

    public static string PathFor(string root, string profile)
        => Path.Combine(root, Sanitize(profile), FileName);

    public static void Write(string root, string profile, ServerRecord record)
    {
        var path = PathFor(root, profile);
        var temporaryPath = path + ".tmp";
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(record));
        File.Move(temporaryPath, path, overwrite: true);
    }

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

    public static void Clear(string root, string profile)
    {
        try
        {
            var path = PathFor(root, profile);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale record is harmless: process identity and start time still have to match.
        }
    }

    public static PortOwner Classify(
        int port,
        bool portInUse,
        ServerRecord? record,
        ProcessEvidence evidence)
    {
        if (!portInUse) return PortOwner.Free;
        if (record is { } candidate
            && candidate.Port == port
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
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(character => invalid.Contains(character) ? '_' : character).ToArray();
        return new string(chars);
    }
}
