using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class OutreachTests : TestContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = RenderComponent<Outreach>();

        Assert.Equal("Outreach", cut.Find("h1").TextContent);
        Assert.Equal("No outreach yet", cut.Find(".wp-empty-state__title").TextContent);
    }
}
