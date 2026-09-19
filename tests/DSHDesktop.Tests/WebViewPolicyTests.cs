using System;
using Xunit;

namespace DSHDesktop.Tests;

public class WebViewPolicyTests
{
    [Theory]
    [InlineData("http://127.0.0.1:3080/?token=secret", true)]
    [InlineData("https://127.0.0.1:4433/path", true)]
    [InlineData("http://localhost:3080/", false)]
    [InlineData("http://[::1]:3080/", false)]
    [InlineData("http://127.0.0.2:3080/", false)]
    [InlineData("http://user:password@127.0.0.1:3080/", false)]
    [InlineData("http://127.0.0.1/", false)]
    [InlineData("file:///C:/temp/index.html", false)]
    [InlineData("not-a-url", false)]
    public void TryCreateTrustedOrigin_RequiresExactIpv4LoopbackWithExplicitPort(string url, bool expected)
    {
        Assert.Equal(expected, WebViewPolicy.TryCreateTrustedOrigin(url, out _));
    }

    [Fact]
    public void TryCreateTrustedOrigin_RemovesCredentialsPathAndQuery()
    {
        Assert.True(WebViewPolicy.TryCreateTrustedOrigin(
            "http://127.0.0.1:3080/login?token=secret#fragment", out var origin));

        Assert.Equal(new Uri("http://127.0.0.1:3080/"), origin);
    }

    [Theory]
    [InlineData("http://127.0.0.1:3080/", "AllowInApp")]
    [InlineData("http://127.0.0.1:3080/session/1", "AllowInApp")]
    [InlineData("http://127.0.0.1:3081/", "OpenExternal")]
    [InlineData("https://example.com/docs", "OpenExternal")]
    [InlineData("file:///C:/Windows/win.ini", "Block")]
    [InlineData("javascript:alert(1)", "Block")]
    [InlineData("not-a-url", "Block")]
    public void ClassifyNavigation_UsesExactOrigin(string target, string expectedName)
    {
        var origin = new Uri("http://127.0.0.1:3080/");
        Assert.Equal(expectedName, WebViewPolicy.ClassifyNavigation(target, origin).ToString());
    }

    [Fact]
    public void ClassifyNavigation_BlocksExternalSubframes()
    {
        var origin = new Uri("http://127.0.0.1:3080/");
        Assert.Equal(
            WebViewPolicy.NavigationDecision.Block,
            WebViewPolicy.ClassifyNavigation("https://example.com/embed", origin, isMainFrame: false));
    }

    [Fact]
    public void ClassifyNavigation_BlocksEverythingBeforeOriginIsEstablished()
    {
        Assert.Equal(
            WebViewPolicy.NavigationDecision.Block,
            WebViewPolicy.ClassifyNavigation("https://example.com", trustedOrigin: null));
    }

    [Theory]
    [InlineData("http://127.0.0.1:3080/export.csv", true)]
    [InlineData("blob:http://127.0.0.1:3080/12345678-1234-1234-1234-123456789abc", true)]
    [InlineData("blob:http://127.0.0.1:3081/12345678-1234-1234-1234-123456789abc", false)]
    [InlineData("blob:https://example.com/id", false)]
    [InlineData("data:text/plain,secret", false)]
    [InlineData("file:///C:/temp/payload.exe", false)]
    public void IsTrustedDownloadSource_AllowsOwnedHttpAndBlobOnly(string source, bool expected)
    {
        var origin = new Uri("http://127.0.0.1:3080/");
        Assert.Equal(expected, WebViewPolicy.IsTrustedDownloadSource(source, origin));
    }
}
