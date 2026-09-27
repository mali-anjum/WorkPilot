using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class JobsTests : BunitContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = Render<Jobs>();

        Assert.Equal("Jobs", cut.Find("h1").TextContent);
        Assert.Equal("No jobs yet", cut.Find(".wp-empty-state__title").TextContent);
    }
}
