using System;
using System.Threading.Tasks;
using Xunit;

namespace DSHDesktop.Tests;

/// <summary>
/// <c>VersionUpdate.cs</c> 中不依赖网络的部分：semver 比较与版本串归一化。
///
/// 明确不在覆盖范围内的（需要真实网络，放进测试会变成 flaky）：
/// <c>CheckAsync</c> 走 registry 之后的路径、<c>FetchReleaseInfoAsync</c>。
/// 这里只测它们在"输入非法 ⇒ 立刻返回 null、完全不碰网络"这一条确定性行为。
/// </summary>
public class VersionUpdateTests
{
    // ---------------------------------------------------------------- semver 比较

    [Theory]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.0.0", "v1.0.0")]           // 容忍 v 前缀
    [InlineData("2.0", "2.0.0")]              // 两段版本补零
    [InlineData("1.0.0+build.5", "1.0.0")]    // 构建元数据不参与比较
    [InlineData("  1.0.0  ", "1.0.0")]        // 前后空白
    public void CompareVersions_ReturnsZero_ForEquivalentVersions(string a, string b)
    {
        Assert.Equal(0, VersionUpdate.CompareVersions(a, b));
    }

    [Fact]
    public void CompareVersions_ComparesMajorNumericallyNotLexically()
    {
        Assert.True(VersionUpdate.CompareVersions("10.0.0", "9.9.9") > 0);
        Assert.True(VersionUpdate.CompareVersions("1.2.0", "1.10.0") < 0);
    }

    [Fact]
    public void CompareVersions_ReleaseIsGreaterThanPrerelease()
    {
        Assert.True(VersionUpdate.CompareVersions("1.0.0", "1.0.0-rc.1") > 0);
        Assert.True(VersionUpdate.CompareVersions("1.0.0-rc.1", "1.0.0") < 0);
    }

    [Fact]
    public void CompareVersions_PrereleaseIdentifiersComparedLexically()
    {
        Assert.True(VersionUpdate.CompareVersions("1.0.0-alpha", "1.0.0-beta") < 0);
    }

    [Fact]
    public void CompareVersions_NumericPrereleaseIdentifiersComparedNumerically()
    {
        // 字符串比较会把 "10" 排到 "2" 前面——这条锁住 semver 的正确语义
        Assert.True(VersionUpdate.CompareVersions("1.0.0-alpha.2", "1.0.0-alpha.10") < 0);
    }

    [Fact]
    public void CompareVersions_NumericIdentifierIsLowerThanAlphanumeric()
    {
        Assert.True(VersionUpdate.CompareVersions("1.0.0-1", "1.0.0-alpha") < 0);
    }

    [Fact]
    public void CompareVersions_MorePrereleaseFieldsWinWhenPrefixIsEqual()
    {
        Assert.True(VersionUpdate.CompareVersions("1.0.0-alpha", "1.0.0-alpha.1") < 0);
    }

    [Fact]
    public void CompareVersions_UnparsableVersion_FallsBackToZero()
    {
        // Parse 把解析不出的段当 0，因此 "abc" 等价于 0.0.0：不抛异常，且低于任何真实版本
        Assert.True(VersionUpdate.CompareVersions("abc", "1.0.0") < 0);
    }

    // ------------------------------------------- 当前版本的预发布后缀（2026-09-12 回归）
    // TryNormalizeVersion 曾经把当前版本的预发布后缀整段砍掉（0.1.5-alpha.1 → 0.1.5），
    // 于是 CompareVersions("0.1.5-rc.2", "0.1.5") 落进"预发布 < 正式版"，判定"已是最新"，
    // 停在预发布上的宿主永远收不到更新。这组用例锁住正确的语义。

    [Theory]
    [InlineData("0.1.5-rc.2", "0.1.5-alpha.1", true)]     // 预发布 → 更新的预发布
    [InlineData("0.1.5-alpha.2", "0.1.5-alpha.1", true)]  // 同一预发布线内递进
    [InlineData("v0.1.5-rc.2", "0.1.5-alpha.1", true)]    // 容忍 v 前缀
    [InlineData("0.1.5", "0.1.5-rc.2", true)]             // 预发布 → 正式版
    [InlineData("0.1.5-rc.2", "0.1.5", false)]            // 已在正式版：不该被"升级"回预发布
    [InlineData("0.1.5-rc.2", "0.1.5-rc.2", false)]       // 同版本
    [InlineData("0.1.4", "0.1.5-alpha.1", false)]         // 版本更低
    [InlineData("", "0.1.5-alpha.1", false)]              // 候选无法解析 → 不报更新
    [InlineData("0.1.5-rc.2", "", false)]                 // 当前无法解析 → 不报更新
    public void IsNewer_RespectsThePrereleaseOfTheCurrentVersion(string candidate, string current, bool expected)
    {
        Assert.Equal(expected, VersionUpdate.IsNewer(candidate, current));
    }

    [Fact]
    public void IsNewer_IgnoresBuildMetadata()
    {
        Assert.False(VersionUpdate.IsNewer("0.1.5+build.7", "0.1.5"));
        Assert.True(VersionUpdate.IsNewer("0.1.5-rc.2+build.7", "0.1.5-alpha.1"));
    }

    // ---------------------------------------------------------------- 不碰网络的确定路径

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("1")]
    [InlineData("not-a-version")]
    public async Task CheckAsync_ReturnsNullForUnparsableCurrentVersion_WithoutNetwork(string current)
    {
        // TryNormalizeVersion 在发起任何 HTTP 请求之前就返回 null（VersionUpdate.cs:144-145），
        // 因此这几条在离线环境下也是确定性的、且是瞬时返回。
        var result = await VersionUpdate.CheckAsync(current);

        Assert.Null(result);
    }

    [Fact]
    public async Task IsReachableAsync_ReturnsFalse_WhenConnectionIsRefused()
    {
        // 回环地址上必然拒绝连接的端口：不依赖外网，且失败路径立即返回
        var reachable = await VersionUpdate.IsReachableAsync("http://127.0.0.1:1/", timeoutMs: 2000);

        Assert.False(reachable);
    }

    [Fact]
    public void ReleasesPageUrl_PointsAtGitHubReleases()
    {
        var url = VersionUpdate.ReleasesPageUrl;

        Assert.StartsWith("https://github.com/", url);
        Assert.EndsWith("/releases", url);
    }
}
