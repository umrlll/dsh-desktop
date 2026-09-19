using System.Collections.Generic;
using DSHDesktop.Core;
using Xunit;

namespace DSHDesktop.Tests;

public class StartupFailureTests
{
    public static IEnumerable<object[]> Cases()
    {
        yield return new object[] { "BackendExited", "backend-exited", "后端" };
        yield return new object[] { "BackendTimeout", "backend-timeout", "超时" };
        yield return new object[] { "UntrustedEndpoint", "untrusted-endpoint", "不受信任" };
        yield return new object[] { "NavigationFailed", "navigation-failed", "导航失败" };
        yield return new object[] { "FrontendTimeout", "frontend-timeout", "启动超时" };
        yield return new object[] { "FrontendProbeFailed", "frontend-probe-failed", "健康确认失败" };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Create_ReturnsStableCodeAndActionableSummary(
        string kindName,
        string code,
        string summaryFragment)
    {
        var kind = System.Enum.Parse<StartupFailure.Kind>(kindName);
        var result = StartupFailure.Create(kind);
        Assert.Equal(kind, result.Kind);
        Assert.Equal(code, result.Code);
        Assert.Contains(summaryFragment, result.Summary);
    }

    [Fact]
    public void Create_AppendsTrimmedDetail()
    {
        var result = StartupFailure.Create(StartupFailure.Kind.BackendExited, "  exit=17  ");
        Assert.EndsWith("：exit=17", result.Summary);
    }
}
