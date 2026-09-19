using Xunit;

namespace DSHDesktop.Tests;

public class FrontendHealthProbeTests
{
    [Theory]
    [InlineData("\"ready\"", "Healthy")]
    [InlineData("\"loading\"", "Loading")]
    [InlineData("\"missing-root\"", "MissingRoot")]
    [InlineData("\"empty-surface\"", "EmptySurface")]
    [InlineData("\"unknown\"", "Invalid")]
    [InlineData("null", "Invalid")]
    [InlineData("not-json", "Invalid")]
    public void Parse_MapsExecuteScriptJson(string value, string expectedName)
    {
        Assert.Equal(expectedName, FrontendHealthProbe.Parse(value).ToString());
    }

    [Fact]
    public void Script_RequiresCompleteDocumentRootAndVisibleContent()
    {
        Assert.Contains("document.readyState", FrontendHealthProbe.Script);
        Assert.Contains("getElementById('root')", FrontendHealthProbe.Script);
        Assert.Contains("getBoundingClientRect", FrontendHealthProbe.Script);
        Assert.Contains("visited < 2048", FrontendHealthProbe.Script);
    }

    [Theory]
    [InlineData("Loading", "页面仍在加载")]
    [InlineData("MissingRoot", "页面缺少 #root 容器")]
    [InlineData("EmptySurface", "页面没有可见的交互内容")]
    [InlineData("Invalid", "页面健康探测返回无效结果")]
    public void Describe_ReturnsActionableFailureDetail(string resultName, string expected)
    {
        var result = System.Enum.Parse<FrontendHealthProbe.Result>(resultName);
        Assert.Equal(expected, FrontendHealthProbe.Describe(result));
    }
}
