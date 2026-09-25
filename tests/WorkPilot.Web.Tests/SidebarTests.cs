using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

// covers: AC-1, AC-2 (nav sections/sub items render, literal routes, active highlight)
public class SidebarTests : TestContext
{
    [Fact]
    public void Renders_all_five_section_labels()
    {
        var cut = RenderComponent<Sidebar>();

        var labels = cut.FindAll(".wp-sidebar__section-label").Select(e => e.TextContent);

        Assert.Equal(["Overview", "Work", "Personal", "Agent", "System"], labels);
    }

    [Fact]
    public void Renders_all_twelve_links_with_their_literal_hrefs()
    {
        var cut = RenderComponent<Sidebar>();

        var links = cut.FindAll(".wp-sidebar__link");

        Assert.Equal(12, links.Count);
        Assert.Contains(links, a => a.GetAttribute("href") == "/jobs" && a.TextContent == "Jobs");
        Assert.Contains(links, a => a.GetAttribute("href") == "/agent/runs" && a.TextContent == "Agent Runs");
    }

    [Fact]
    public void Highlights_dashboard_as_active_on_the_root_route()
    {
        var cut = RenderComponent<Sidebar>();

        var dashboard = cut.FindAll(".wp-sidebar__link").Single(a => a.TextContent == "Dashboard");

        Assert.Contains("active", dashboard.ClassList);
    }

    [Fact]
    public void Highlights_the_matching_link_after_navigating_and_clears_the_previous_one()
    {
        var cut = RenderComponent<Sidebar>();
        var nav = Services.GetRequiredService<NavigationManager>();

        nav.NavigateTo("jobs");
        cut.Render();

        var links = cut.FindAll(".wp-sidebar__link");
        var jobs = links.Single(a => a.TextContent == "Jobs");
        var dashboard = links.Single(a => a.TextContent == "Dashboard");

        Assert.Contains("active", jobs.ClassList);
        Assert.DoesNotContain("active", dashboard.ClassList);
    }
}
