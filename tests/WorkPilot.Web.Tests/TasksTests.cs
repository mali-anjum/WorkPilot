using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

public class TasksPageTests : TestContext
{
    [Fact]
    public void Renders_its_page_header_and_empty_state()
    {
        var cut = RenderComponent<Tasks>();

        Assert.Equal("Tasks", cut.Find("h1").TextContent);
        Assert.Equal("No tasks yet", cut.Find(".wp-empty-state__title").TextContent);
    }
}
