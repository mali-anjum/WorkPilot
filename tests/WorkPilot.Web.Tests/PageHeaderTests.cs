using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

public class PageHeaderTests : TestContext
{
    [Fact]
    public void Renders_the_title_as_the_pages_h1()
    {
        var cut = RenderComponent<PageHeader>(parameters => parameters
            .Add(p => p.Title, "Jobs"));

        Assert.Equal("Jobs", cut.Find("h1").TextContent);
    }

    [Fact]
    public void Renders_the_description_when_given_one()
    {
        var cut = RenderComponent<PageHeader>(parameters => parameters
            .Add(p => p.Title, "Jobs")
            .Add(p => p.Description, "Everything matched or tracked."));

        Assert.Equal("Everything matched or tracked.", cut.Find(".wp-page-header__description").TextContent);
    }

    [Fact]
    public void Omits_the_description_element_when_none_is_given()
    {
        var cut = RenderComponent<PageHeader>(parameters => parameters
            .Add(p => p.Title, "Jobs"));

        Assert.Empty(cut.FindAll(".wp-page-header__description"));
    }
}
