using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

public class EmptyStateTests : TestContext
{
    [Fact]
    public void Renders_the_required_title()
    {
        var cut = RenderComponent<EmptyState>(parameters => parameters
            .Add(p => p.Title, "No jobs yet"));

        Assert.Equal("No jobs yet", cut.Find(".wp-empty-state__title").TextContent);
    }

    [Fact]
    public void Renders_the_description_when_given_one()
    {
        var cut = RenderComponent<EmptyState>(parameters => parameters
            .Add(p => p.Title, "No jobs yet")
            .Add(p => p.Description, "New matches will show up here."));

        Assert.Equal("New matches will show up here.", cut.Find(".wp-empty-state__description").TextContent);
    }

    [Fact]
    public void Omits_the_description_element_when_none_is_given()
    {
        var cut = RenderComponent<EmptyState>(parameters => parameters
            .Add(p => p.Title, "No jobs yet"));

        Assert.Empty(cut.FindAll(".wp-empty-state__description"));
    }

    [Fact]
    public void Renders_the_icon_as_decorative_and_hidden_from_assistive_tech()
    {
        var cut = RenderComponent<EmptyState>(parameters => parameters
            .Add(p => p.Title, "No jobs yet")
            .Add(p => p.Icon, "🗂"));

        var icon = cut.Find(".wp-empty-state__icon");
        Assert.Equal("true", icon.GetAttribute("aria-hidden"));
    }

    [Fact]
    public void Omits_the_icon_element_when_none_is_given()
    {
        var cut = RenderComponent<EmptyState>(parameters => parameters
            .Add(p => p.Title, "No jobs yet"));

        Assert.Empty(cut.FindAll(".wp-empty-state__icon"));
    }
}
