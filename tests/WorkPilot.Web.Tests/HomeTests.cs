using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class HomeTests : TestContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = RenderComponent<Home>();

        Assert.Equal("Dashboard", cut.Find("h1").TextContent);
        Assert.Equal("No activity yet", cut.Find(".wp-empty-state__title").TextContent);
    }
}
