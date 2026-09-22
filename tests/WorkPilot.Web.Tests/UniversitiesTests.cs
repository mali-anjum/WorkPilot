using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class UniversitiesTests : TestContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = RenderComponent<Universities>();

        Assert.Equal("Universities", cut.Find("h1").TextContent);
        Assert.Equal("No universities yet", cut.Find(".wp-empty-state__title").TextContent);
    }
}
