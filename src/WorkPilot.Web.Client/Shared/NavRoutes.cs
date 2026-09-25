namespace WorkPilot.Web.Client.Shared;

/// <summary>A sub item under a top level nav section, with its literal route.</summary>
public sealed record NavItem(string Label, string Href);

/// <summary>A top level nav section; a group label only, never itself routable.</summary>
public sealed record NavSection(string Label, IReadOnlyList<NavItem> Items);

/// <summary>
/// The shell's nav structure and the literal route map, per spec 0003. Hardcoded here
/// (not derived from any data source); later features add real content behind each
/// route without changing this table's shape.
/// </summary>
public static class NavRoutes
{
    public static readonly IReadOnlyList<NavSection> Sections =
    [
        new("Overview", [new NavItem("Dashboard", "/")]),
        new("Work",
        [
            new NavItem("Jobs", "/jobs"),
            new NavItem("Applications", "/applications"),
            new NavItem("Resumes", "/resumes"),
            new NavItem("Universities", "/universities"),
            new NavItem("Outreach", "/outreach"),
        ]),
        new("Personal",
        [
            new NavItem("Calendar", "/calendar"),
            new NavItem("Tasks", "/tasks"),
        ]),
        new("Agent",
        [
            new NavItem("Agent Runs", "/agent/runs"),
            new NavItem("Approvals", "/approvals"),
        ]),
        new("System",
        [
            new NavItem("Settings", "/settings"),
            new NavItem("Integrations", "/integrations"),
        ]),
    ];
}
