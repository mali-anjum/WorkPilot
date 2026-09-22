using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class CalendarTests : TestContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = RenderComponent<Calendar>();

        Assert.Equal("Calendar", cut.Find("h1").TextContent);
        Assert.Equal("No events yet", cut.Find(".wp-empty-state__title").TextContent);
    }
}
