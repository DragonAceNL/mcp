namespace Tomato.Mcp;

/// <summary>Holds one <see cref="TomatoClient"/> per configured router and resolves a selector.</summary>
public sealed class TomatoManager
{
    private readonly Dictionary<string, TomatoClient> _routers = new(StringComparer.OrdinalIgnoreCase);

    public TomatoManager(IEnumerable<TomatoRouterConfig> configs)
    {
        foreach (var cfg in configs)
            _routers[cfg.Name] = new TomatoClient(cfg);
    }

    public IReadOnlyList<(string Name, string Host)> List()
        => _routers.Values.Select(r => (r.Name, r.Host)).ToList();

    public TomatoClient Get(string? selector)
    {
        if (string.IsNullOrEmpty(selector))
        {
            if (_routers.Count == 1) return _routers.Values.First();
            throw new InvalidOperationException(
                $"Multiple routers configured; specify \"router\" (one of: {string.Join(", ", _routers.Values.Select(r => r.Name))})");
        }
        if (_routers.TryGetValue(selector, out var byName)) return byName;
        var byHost = _routers.Values.FirstOrDefault(r => r.Host == selector);
        if (byHost is not null) return byHost;
        throw new InvalidOperationException(
            $"Unknown router \"{selector}\". Configured: {string.Join(", ", _routers.Values.Select(r => $"{r.Name} ({r.Host})"))}");
    }

    public void DisconnectAll()
    {
        foreach (var r in _routers.Values) r.Disconnect();
    }
}
