namespace Windows.Mcp;

/// <summary>Holds one <see cref="WindowsClient"/> per configured server and resolves a selector.</summary>
public sealed class WindowsManager
{
    private readonly Dictionary<string, WindowsClient> _servers = new(StringComparer.OrdinalIgnoreCase);

    public WindowsManager(IEnumerable<WindowsServerConfig> configs)
    {
        foreach (var cfg in configs)
            _servers[cfg.Name] = new WindowsClient(cfg);
    }

    public IReadOnlyList<(string Name, string Host)> List()
        => _servers.Values.Select(s => (s.Name, s.Host)).ToList();

    public WindowsClient Get(string? selector)
    {
        if (string.IsNullOrEmpty(selector))
        {
            if (_servers.Count == 1) return _servers.Values.First();
            throw new InvalidOperationException(
                $"Multiple servers configured; specify \"server\" (one of: {string.Join(", ", _servers.Values.Select(s => s.Name))})");
        }
        if (_servers.TryGetValue(selector, out var byName)) return byName;
        var byHost = _servers.Values.FirstOrDefault(s => s.Host == selector);
        if (byHost is not null) return byHost;
        throw new InvalidOperationException(
            $"Unknown server \"{selector}\". Configured: {string.Join(", ", _servers.Values.Select(s => $"{s.Name} ({s.Host})"))}");
    }

    public void DisconnectAll()
    {
        foreach (var s in _servers.Values) s.Disconnect();
    }
}
