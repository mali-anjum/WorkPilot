using System.Text.Json;
using System.Text.RegularExpressions;

namespace WorkPilot.Api.Tests;

// Repository guards for the AI provider rules (spec 0006): the committed config
// ships on the Fake provider with no key, and only WorkPilot.AI knows any vendor
// SDK. Reads the files from the repository, so a key pasted into appsettings or
// an OpenAI package added to another project fails here.
public class AiConfigGuardTests
{
    private static readonly string RepoRoot = FindRepoRoot();

    // covers: spec 0006 AC-3, Key invariants (the committed appsettings.json never holds an API key)
    [Fact]
    public void Committed_api_settings_default_to_Fake_and_hold_no_key()
    {
        var files = Directory.GetFiles(Path.Combine(RepoRoot, "src", "WorkPilot.Api"), "appsettings*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            Assert.Empty(PropertiesNamed(document.RootElement, "ApiKey").Where(v => v.ValueKind != JsonValueKind.Null));
        }

        using var main = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "src", "WorkPilot.Api", "appsettings.json")));
        var defaultProvider = main.RootElement.GetProperty("Ai").GetProperty("Purposes").GetProperty("Default").GetProperty("Provider").GetString();
        Assert.Equal("Fake", defaultProvider);
    }

    // covers: spec 0006 Key invariants (no code outside WorkPilot.AI names a vendor or SDK type)
    [Fact]
    public void Only_the_AI_project_references_a_vendor_AI_package()
    {
        var offenders = Directory.GetFiles(Path.Combine(RepoRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != "WorkPilot.AI.csproj")
            .Where(p => Regex.IsMatch(File.ReadAllText(p), @"PackageReference\s+Include=""(OpenAI|Microsoft\.Extensions\.AI\.OpenAI|Azure\.AI\.|Anthropic|Google\.|OllamaSharp)"))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Empty(offenders);
    }

    private static IEnumerable<JsonElement> PropertiesNamed(JsonElement element, string name)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(name))
                {
                    yield return property.Value;
                }

                foreach (var nested in PropertiesNamed(property.Value, name))
                {
                    yield return nested;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray().SelectMany(i => PropertiesNamed(i, name)))
            {
                yield return item;
            }
        }
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
}
