using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AiRaccoon.Infrastructure.Embedding;

/// <summary>
///     Reads ONNX Runtime's verbose "Node placements" block, and CoreML's GetCapability line, from one
///     session's log lines and summarizes which execution provider got which nodes.
/// </summary>
internal sealed partial class NodePlacement
{
    private const string ProviderSuffix = "ExecutionProvider";
    private const string CpuProvider = "CPUExecutionProvider";
    private const string CoreMlProvider = "CoreMLExecutionProvider";

    private readonly List<Provider> _providers = [];
    private Provider? _current;
    private (int Partitions, int Total, int Supported)? _coreMl;

    /// <summary>Feeds one ORT log message, in the order ORT wrote them.</summary>
    public void Add(string message)
    {
        if (PlacedOn().Match(message) is { Success: true } placed)
        {
            _current = new Provider(placed.Groups["provider"].Value, Number(placed.Groups["count"]), placed.Groups["all"].Success);
            _providers.Add(_current);
            return;
        }

        if (_current is not null && NodeLine().Match(message) is { Success: true } node)
        {
            _current.Count(node.Groups["op"].Value);
            return;
        }

        _current = null;
        if (CoreMlCapability().Match(message) is { Success: true } capability)
        {
            _coreMl = (Number(capability.Groups["partitions"]), Number(capability.Groups["total"]), Number(capability.Groups["supported"]));
        }
    }

    /// <summary>One line per session, e.g. "CoreML: 305/400 original nodes in 37 fused partitions; CPU: 95 — Reshape×48, Neg×24";
    /// null when ORT reported no placement.</summary>
    public string? Summary()
    {
        if (_providers.Count == 0)
        {
            return null;
        }

        var ordered = _providers
            .OrderBy(p => p.Name == CpuProvider)
            .ThenByDescending(p => p.Nodes);
        return string.Join("; ", ordered.Select(Describe));
    }

    /// <summary>Whether a message is CoreML's GetCapability line, which <see cref="Summary" /> already carries.</summary>
    public static bool IsCoreMlCapability(string message) => CoreMlCapability().IsMatch(message);

    private string Describe(Provider provider)
    {
        var name = provider.Name.EndsWith(ProviderSuffix, StringComparison.Ordinal)
            ? provider.Name[..^ProviderSuffix.Length]
            : provider.Name;
        if (provider.All)
        {
            return provider.Nodes == 1 && provider.Name != CpuProvider
                ? $"{name}: the whole graph as 1 fused node"
                : $"{name}: all {provider.Nodes} nodes";
        }

        if (provider.Name == CoreMlProvider && _coreMl is var (partitions, total, supported) && provider.Fused == provider.Nodes)
        {
            return $"{name}: {supported}/{total} original nodes in {Partitions(partitions)}";
        }

        if (provider.Fused == provider.Nodes)
        {
            return $"{name}: {Partitions(provider.Fused)}";
        }

        var parts = new List<string>();
        if (provider.Fused > 0)
        {
            parts.Add(Partitions(provider.Fused));
        }

        parts.AddRange(provider.Ops
            .OrderByDescending(op => op.Count)
            .Select(op => op.Count == 1 ? op.Name : $"{op.Name}×{op.Count}"));
        return new StringBuilder($"{name}: {provider.Nodes} — ").AppendJoin(", ", parts).ToString();
    }

    private static string Partitions(int count) => count == 1 ? "1 fused partition" : $"{count} fused partitions";

    private static int Number(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

    /// <summary>One provider's share of the graph. Fused nodes are the partitions a provider compiled; their op type
    /// carries an underscore, which no ONNX or contrib op type does.</summary>
    private sealed class Provider(string name, int nodes, bool all)
    {
        private readonly Dictionary<string, (string Name, int Count, int Order)> _ops = new(StringComparer.Ordinal);

        public string Name { get; } = name;
        public int Nodes { get; } = nodes;
        public bool All { get; } = all;
        public int Fused { get; private set; }

        public IEnumerable<(string Name, int Count)> Ops =>
            _ops.Values.OrderBy(op => op.Order).Select(op => (op.Name, op.Count));

        public void Count(string opType)
        {
            if (opType.Contains('_', StringComparison.Ordinal))
            {
                Fused++;
                return;
            }

            _ops[opType] = _ops.TryGetValue(opType, out var seen)
                ? seen with { Count = seen.Count + 1 }
                : (opType, 1, _ops.Count);
        }
    }

    [GeneratedRegex(@"^\s*(?:(?<all>All nodes)|Node\(s\)) placed on \[(?<provider>[^\]]+)\]\. Number of nodes: (?<count>\d+)")]
    private static partial Regex PlacedOn();

    [GeneratedRegex(@"^  (?<op>\S+) \(")]
    private static partial Regex NodeLine();

    [GeneratedRegex(@"number of partitions supported by CoreML: (?<partitions>\d+) number of nodes in the graph: (?<total>\d+) number of nodes supported by CoreML: (?<supported>\d+)")]
    private static partial Regex CoreMlCapability();
}
