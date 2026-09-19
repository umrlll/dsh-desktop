namespace DSHDesktop.Core;

/// <summary>供日志、UI、诊断和测试共同使用的稳定启动失败分类。</summary>
public static class StartupFailure
{
    public enum Kind
    {
        BackendExited,
        BackendTimeout,
        UntrustedEndpoint,
        NavigationFailed,
        FrontendTimeout,
        FrontendProbeFailed,
    }

    public readonly record struct Detail(Kind Kind, string Code, string Summary);

    public static Detail Create(Kind kind, string? detail = null)
    {
        var (code, summary) = kind switch
        {
            Kind.BackendExited => ("backend-exited", "DSH 后端在就绪前退出"),
            Kind.BackendTimeout => ("backend-timeout", "等待 DSH 后端就绪超时"),
            Kind.UntrustedEndpoint => ("untrusted-endpoint", "DSH 返回了不受信任的启动地址"),
            Kind.NavigationFailed => ("navigation-failed", "受信任页面导航失败"),
            Kind.FrontendTimeout => ("frontend-timeout", "DSH 前端交互面启动超时"),
            Kind.FrontendProbeFailed => ("frontend-probe-failed", "DSH 前端健康确认失败"),
            _ => ("unknown", "未知启动失败"),
        };

        if (!string.IsNullOrWhiteSpace(detail)) summary += "：" + detail.Trim();
        return new Detail(kind, code, summary);
    }
}
