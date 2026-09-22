using WorkPilot.Web.Components.Pages;

namespace WorkPilot.Web.Tests;

// covers: AC-3 (all 10 in-scope components live on /design)
public class DesignTests : TestContext
{
    public DesignTests()
    {
        // The Modal example calls JS interop (focus trap) on open.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Renders_a_card_for_every_in_scope_component()
    {
        var cut = RenderComponent<Design>();

        var cardTitles = cut.FindAll(".wp-card__title").Select(e => e.TextContent);

        Assert.Equal(
            [
                "Button",
                "Badge / StatusBadge",
                "EmptyState",
                "PageHeader",
                "Modal",
                "CommandPalette",
                "AppShell / Sidebar / TopBar",
            ],
            cardTitles);
    }

    [Fact]
    public void The_example_modal_starts_closed_and_opens_on_click()
    {
        var cut = RenderComponent<Design>();

        Assert.Empty(cut.FindAll("[role='dialog']"));

        var openButton = cut.FindAll("button").Single(b => b.TextContent == "Open modal");
        openButton.Click();

        Assert.Single(cut.FindAll("[role='dialog']"));
    }
}
