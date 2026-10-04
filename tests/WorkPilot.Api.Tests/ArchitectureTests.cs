using System.Reflection;
using System.Text.RegularExpressions;
using WorkPilot.Application.Common;
using WorkPilot.Domain.Common;
using WorkPilot.Infrastructure.Modules.Audit.Outbox;

namespace WorkPilot.Api.Tests;

// The dependency rule (AGENTS.md, Clean Architecture) and the event naming rules
// (spec 0018, sections 2 and 3), read from the compiled assemblies. This is the
// architecture test spec 0018 lists under Enforcement; like that spec says, it
// cannot see a direct write to another module's DbSet, which stays a review rule.
public partial class ArchitectureTests
{
    private static readonly Assembly Domain = typeof(IDomainEvent).Assembly;
    private static readonly Assembly Application = typeof(Result<>).Assembly;
    private static readonly Assembly Infrastructure = typeof(EventRegistry).Assembly;

    private static readonly string[] Modules =
    [
        "identity", "profile", "jobs", "applications", "universities", "outreach", "calendar",
        "tasks", "agent", "approvals", "integrations", "notifications", "audit",
    ];

    // covers: AGENTS.md Rules ("Domain has zero external imports")
    [Fact]
    public void Domain_references_nothing_but_the_runtime()
    {
        var foreign = References(Domain).Where(n => !IsRuntime(n)).ToList();

        Assert.Empty(foreign);
    }

    // covers: AGENTS.md Rules (no framework or ORM code inside Application; outer layers depend inward)
    [Fact]
    public void Application_references_no_framework_ORM_or_outer_layer()
    {
        var forbidden = References(Application)
            .Where(n => n.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal)
                || n.StartsWith("Npgsql", StringComparison.Ordinal)
                || n.StartsWith("Hangfire", StringComparison.Ordinal)
                || n.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal)
                || n is "WorkPilot.Infrastructure" or "WorkPilot.Workers" or "WorkPilot.AI" or "WorkPilot.Api" or "WorkPilot.Web" or "WorkPilot.Web.Client")
            .ToList();

        Assert.Empty(forbidden);
    }

    // covers: AGENTS.md Rules (outer layers depend on inner layers, never the reverse)
    [Fact]
    public void Infrastructure_never_references_a_presentation_or_worker_host()
    {
        var forbidden = References(Infrastructure)
            .Where(n => n is "WorkPilot.Workers" or "WorkPilot.Api" or "WorkPilot.Web" or "WorkPilot.Web.Client")
            .ToList();

        Assert.Empty(forbidden);
    }

    // covers: spec 0018 section 2 (an event lives in its source module and is named <module>.<event-kebab>.v<n>)
    [Fact]
    public void Every_domain_event_has_a_stable_versioned_name_in_its_own_module()
    {
        var events = Domain.GetTypes().Where(t => typeof(IDomainEvent).IsAssignableFrom(t) && t is { IsInterface: false, IsAbstract: false }).ToList();

        Assert.NotEmpty(events);
        Assert.All(events, type =>
        {
            var name = EventRegistry.NameOf(type);
            var match = EventName().Match(name);
            Assert.True(match.Success, $"{type.Name}: \"{name}\" is not <module>.<event-kebab>.v<n>");
            Assert.Equal(ModuleOf(type), match.Groups["module"].Value);
        });
    }

    // covers: spec 0018 section 2 (a handler lives in the subscriber module and is keyed <module>.<handler-kebab>)
    [Fact]
    public void Every_event_handler_has_a_stable_key_in_its_own_module()
    {
        var handlers = new[] { Application, Infrastructure }
            .SelectMany(a => a.GetTypes())
            .Where(t => t is { IsInterface: false, IsAbstract: false }
                && t.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IEventHandler<>)))
            .ToList();

        Assert.NotEmpty(handlers);
        Assert.All(handlers, type =>
        {
            var key = (string?)type.GetProperty("HandlerKey", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            Assert.NotNull(key);
            var match = HandlerKey().Match(key);
            Assert.True(match.Success, $"{type.Name}: \"{key}\" is not <module>.<handler-kebab>");
            Assert.Equal(ModuleOf(type), match.Groups["module"].Value);
        });
    }

    private static IEnumerable<string> References(Assembly assembly) =>
        assembly.GetReferencedAssemblies().Select(a => a.Name!);

    private static bool IsRuntime(string name) =>
        name is "netstandard" or "mscorlib" || name.StartsWith("System", StringComparison.Ordinal);

    // The module is the segment after "Modules" in the namespace, e.g. WorkPilot.Domain.Modules.Jobs.Matching -> jobs.
    private static string ModuleOf(Type type)
    {
        var parts = type.Namespace!.Split('.');
        var index = Array.IndexOf(parts, "Modules");
        Assert.True(index >= 0 && index + 1 < parts.Length, $"{type.FullName} is not inside a Modules/<Area> folder");
        var module = parts[index + 1].ToLowerInvariant();
        Assert.Contains(module, Modules);
        return module;
    }

    [GeneratedRegex(@"^(?<module>[a-z]+)\.[a-z0-9]+(-[a-z0-9]+)*\.v[1-9][0-9]*$")]
    private static partial Regex EventName();

    [GeneratedRegex(@"^(?<module>[a-z]+)\.[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex HandlerKey();
}
