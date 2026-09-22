using WorkPilot.Web.Client.Shared;

namespace WorkPilot.Web.Tests;

// covers: AC-2 (nav sections/sub items, literal routes, per spec 0003's Route map)
public class NavRoutesTests
{
    [Fact]
    public void Has_the_five_sections_in_order()
    {
        Assert.Equal(
            ["Overview", "Work", "Personal", "Agent", "System"],
            NavRoutes.Sections.Select(s => s.Label));
    }

    [Fact]
    public void Has_eleven_sub_items_total()
    {
        Assert.Equal(11, NavRoutes.Sections.Sum(s => s.Items.Count));
    }

    [Fact]
    public void Every_route_matches_the_specs_literal_route_map()
    {
        var expected = new Dictionary<string, string>
        {
            ["Dashboard"] = "/",
            ["Jobs"] = "/jobs",
            ["Applications"] = "/applications",
            ["Universities"] = "/universities",
            ["Outreach"] = "/outreach",
            ["Calendar"] = "/calendar",
            ["Tasks"] = "/tasks",
            ["Agent Runs"] = "/agent/runs",
            ["Approvals"] = "/approvals",
            ["Settings"] = "/settings",
            ["Integrations"] = "/integrations",
        };

        var actual = NavRoutes.Sections
            .SelectMany(s => s.Items)
            .ToDictionary(i => i.Label, i => i.Href);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void No_two_routes_share_the_same_href()
    {
        var hrefs = NavRoutes.Sections.SelectMany(s => s.Items).Select(i => i.Href).ToList();

        Assert.Equal(hrefs.Count, hrefs.Distinct().Count());
    }
}
