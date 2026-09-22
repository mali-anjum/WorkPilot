using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

public class CardTests : TestContext
{
    [Fact]
    public void Renders_a_title_when_given_one()
    {
        var cut = RenderComponent<Card>(parameters => parameters
            .Add(p => p.Title, "Recent applications")
            .AddChildContent("<p>Body</p>"));

        Assert.Equal("Recent applications", cut.Find(".wp-card__title").TextContent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Omits_the_title_element_when_title_is_null_or_blank(string? title)
    {
        var cut = RenderComponent<Card>(parameters => parameters
            .Add(p => p.Title, title)
            .AddChildContent("<p>Body</p>"));

        Assert.Empty(cut.FindAll(".wp-card__title"));
    }

    [Fact]
    public void Renders_its_child_content_in_the_body()
    {
        var cut = RenderComponent<Card>(parameters => parameters
            .AddChildContent("<p>Applied to 3 jobs this week</p>"));

        Assert.Equal("Applied to 3 jobs this week", cut.Find(".wp-card__body p").TextContent);
    }
}
