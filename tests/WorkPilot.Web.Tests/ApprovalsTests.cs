using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class ApprovalsTests : TestContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = RenderComponent<Approvals>();

        Assert.Equal("Approvals", cut.Find("h1").TextContent);
        Assert.Equal("Nothing pending", cut.Find(".wp-empty-state__title").TextContent);
    }
}
