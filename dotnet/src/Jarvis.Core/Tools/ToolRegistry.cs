using System.Collections.Generic;
using System.Linq;

namespace Jarvis.Core.Tools;

/// <summary>
/// A name → tool lookup populated at startup from the user's config.yaml
/// tools section. Backends ask for <see cref="AllSchemas"/> to build the
/// LLM's tools= parameter and <see cref="TryGet"/> when a tool call comes
/// back to execute.
/// </summary>
public sealed class ToolRegistry
{
    private readonly Dictionary<string, ITool> _tools;

    public ToolRegistry(IEnumerable<ITool> tools)
    {
        _tools = tools.ToDictionary(t => t.Schema.Name, t => t);
    }

    public IReadOnlyList<ToolSchema> AllSchemas =>
        _tools.Values.Select(t => t.Schema).ToList();

    public bool TryGet(string name, out ITool? tool) =>
        _tools.TryGetValue(name, out tool);

    public IReadOnlyList<ITool> AllTools => _tools.Values.ToList();
}
