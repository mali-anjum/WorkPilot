using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class AgentRunsTests : TestContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = RenderComponent<AgentRuns>();

        Assert.Equal("Agent Runs", cut.Find("h1").TextContent);
        Assert.Equal("No agent runs yet", cut.Find(".wp-empty-state__title").TextContent);
    }
}
