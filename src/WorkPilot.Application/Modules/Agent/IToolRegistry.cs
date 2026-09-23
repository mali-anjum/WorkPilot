using System.Diagnostics.CodeAnalysis;
using WorkPilot.Domain.Modules.Agent;

namespace WorkPilot.Application.Modules.Agent;

/// <summary>Every DI-registered <see cref="ITool"/>, looked up by name (spec 0005).</summary>
public interface IToolRegistry
{
    bool TryGet(string name, [NotNullWhen(true)] out ITool? tool);

    IReadOnlyCollection<ITool> All { get; }
}

/// <summary>Aggregates every DI-registered <see cref="ITool"/> into a name-keyed lookup.</summary>
public sealed class ToolRegistry : IToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Name, StringComparer.Ordinal);
    }

    public bool TryGet(string name, [NotNullWhen(true)] out ITool? tool) => _tools.TryGetValue(name, out tool);

    public IReadOnlyCollection<ITool> All => _tools.Values;
}
