using System.Text.RegularExpressions;

namespace WorkPilot.Web.Tests;

// The design tokens rule (spec 0003): the shared components read colors from the
// tokens in tokens.css instead of hard coding them, and the focus ring every
// interactive element shows comes from a token too. Reads the stylesheets from
// the repository, so a hard coded color added to a component fails here.
public partial class DesignTokenCssTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // covers: spec 0003 AC-5
    [Fact]
    public void Shared_component_styles_use_no_hex_color_literals()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot, "src", "WorkPilot.Web.Client", "Shared"), "*.razor.css");

        Assert.NotEmpty(files);
        var offenders = files
            .SelectMany(f => File.ReadAllLines(f).Select((line, i) => (File: Path.GetFileName(f), Line: i + 1, Text: line)))
            .Where(l => HexColor().IsMatch(l.Text))
            .Select(l => $"{l.File}:{l.Line} {l.Text.Trim()}")
            .ToList();

        Assert.Empty(offenders);
    }

    // covers: spec 0003 AC-4 (the focus ring rule), AC-5
    [Fact]
    public void Tokens_define_a_global_focus_visible_outline_from_a_token()
    {
        var css = File.ReadAllText(Path.Combine(RepoRoot, "src", "WorkPilot.Web", "wwwroot", "css", "tokens.css"));

        var rule = Regex.Match(css, @"(?m)^:focus-visible\s*\{(?<body>[^}]*)\}");

        Assert.True(rule.Success, "tokens.css has no global :focus-visible rule");
        Assert.Matches(@"outline\s*:[^;]*var\(--", rule.Groups["body"].Value);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WorkPilot.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("WorkPilot.slnx not found above the test output folder.");
    }

    [GeneratedRegex(@"#[0-9a-fA-F]{3,8}\b")]
    private static partial Regex HexColor();
}
