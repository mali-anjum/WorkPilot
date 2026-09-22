using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class IntegrationsTests : TestContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = RenderComponent<Integrations>();

        Assert.Equal("Integrations", cut.Find("h1").TextContent);
        Assert.Equal("No integrations connected", cut.Find(".wp-empty-state__title").TextContent);
    }
}
