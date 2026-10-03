using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

// covers: AC-6 (theme toggle at the Blazor level; the cookie/no-flash half is curl/browser
// verified in verify.md, out of reach for a bUnit render)
public class TopBarTests : BunitContext
{
    public TopBarTests()
    {
        // Loose mode: the toggle's JS interop (import + setTheme) doesn't need a real
        // browser to exercise the component's own state and markup.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void Starts_in_sync_with_the_server_resolved_initial_theme()
    {
        var cut = Render<TopBar>(parameters => parameters
            .Add(p => p.InitialTheme, "light"));

        var button = cut.Find("button");
        Assert.Equal("Switch to dark mode", button.GetAttribute("aria-label"));
        Assert.Equal("☀ Light", button.TextContent);
    }

    [Fact]
    public void Defaults_to_dark_when_no_initial_theme_is_given()
    {
        var cut = Render<TopBar>();

        Assert.Equal("Switch to light mode", cut.Find("button").GetAttribute("aria-label"));
    }

    [Fact]
    public void Clicking_the_toggle_flips_the_label_and_aria_label()
    {
        var cut = Render<TopBar>(parameters => parameters
            .Add(p => p.InitialTheme, "dark"));

        cut.Find("button").Click();

        var button = cut.Find("button");
        Assert.Equal("☀ Light", button.TextContent);
        Assert.Equal("Switch to dark mode", button.GetAttribute("aria-label"));
    }

    [Fact]
    public void Clicking_twice_returns_to_the_starting_theme()
    {
        var cut = Render<TopBar>(parameters => parameters
            .Add(p => p.InitialTheme, "dark"));

        cut.Find("button").Click();
        cut.Find("button").Click();

        Assert.Equal("☾ Dark", cut.Find("button").TextContent);
    }

    // Spec 0020: the host puts the notification bell in the top bar through this slot.
    [Fact]
    public void Actions_render_in_the_top_bar_before_the_theme_toggle()
    {
        var cut = Render<TopBar>(parameters => parameters
            .Add(p => p.Actions, builder => builder.AddMarkupContent(0, "<span id=\"slot\">bell</span>")));

        var actions = cut.Find(".wp-topbar__actions");
        Assert.Equal("slot", actions.FirstElementChild!.Id);
    }
}
