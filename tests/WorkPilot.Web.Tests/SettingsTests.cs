using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class SettingsTests : BunitContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = Render<Settings>();

        Assert.Equal("Settings", cut.Find("h1").TextContent);
        Assert.Equal("Nothing to configure yet", cut.Find(".wp-empty-state__title").TextContent);
    }
}
