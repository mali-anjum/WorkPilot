using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

// covers: AC-1 (shell renders sidebar, top bar, and the routed page content together)
public class AppShellTests : TestContext
{
    public AppShellTests()
    {
        // TopBar and CommandPalette both call JS interop on render.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Renders_the_sidebar_topbar_and_child_content_together()
    {
        var cut = RenderComponent<AppShell>(parameters => parameters
            .AddChildContent("<p>Routed page content</p>"));

        Assert.Single(cut.FindAll(".wp-sidebar"));
        Assert.Single(cut.FindAll(".wp-topbar"));
        Assert.Equal("Routed page content", cut.Find(".wp-app-shell__content p").TextContent);
    }

    [Fact]
    public void The_command_palette_starts_closed()
    {
        var cut = RenderComponent<AppShell>();

        Assert.Empty(cut.FindAll("[role='dialog']"));
    }

    [Fact]
    public void Forwards_the_initial_theme_down_to_the_top_bar()
    {
        var cut = RenderComponent<AppShell>(parameters => parameters
            .Add(p => p.InitialTheme, "light"));

        Assert.Equal("Switch to dark mode", cut.Find(".wp-topbar button").GetAttribute("aria-label"));
    }
}
