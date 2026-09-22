using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

public class BadgeTests : TestContext
{
    [Fact]
    public void Renders_its_child_content()
    {
        var cut = RenderComponent<Badge>(parameters => parameters
            .AddChildContent("Applied"));

        Assert.Equal("Applied", cut.Find("span").TextContent);
    }

    [Fact]
    public void Defaults_to_the_neutral_status_class()
    {
        var cut = RenderComponent<Badge>(parameters => parameters
            .AddChildContent("Draft"));

        Assert.Contains("wp-badge--neutral", cut.Find("span").ClassList);
    }

    [Theory]
    [InlineData(StatusKind.Success, "wp-badge--success")]
    [InlineData(StatusKind.Warning, "wp-badge--warning")]
    [InlineData(StatusKind.Danger, "wp-badge--danger")]
    [InlineData(StatusKind.Info, "wp-badge--info")]
    public void Maps_each_status_to_its_own_token_class(StatusKind status, string expectedClass)
    {
        var cut = RenderComponent<Badge>(parameters => parameters
            .Add(p => p.Status, status)
            .AddChildContent("Label"));

        Assert.Contains(expectedClass, cut.Find("span").ClassList);
    }
}
