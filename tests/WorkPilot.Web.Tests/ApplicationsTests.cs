using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class ApplicationsTests : TestContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = RenderComponent<Applications>();

        Assert.Equal("Applications", cut.Find("h1").TextContent);
        Assert.Equal("No applications yet", cut.Find(".wp-empty-state__title").TextContent);
    }
}
