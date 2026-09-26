using System.Text.Json;

namespace Tasmota.Mcp;

public sealed class TasmotaManagerOptions
{
    public string? DefaultUser { get; init; }
    public string? DefaultPassword { get; init; }
    public IReadOnlyList<string> ScanTargets { get; init; } = Array.Empty<string>();
    public int ScanConcurrency { get; init; } = 128;
    public int ScanTimeoutMs { get; init; } = 800;
    public string? CacheFile { get; init; }
}

/// <summary>
/// Holds explicit + discovered Tasmota devices, resolves a device by name/host
/// (with an ad-hoc fallback for any valid IP), and scans configured subnets.
/// </summary>
public sealed class TasmotaManager
{
    private readonly Dictionary<string, TasmotaClient> _explicit = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TasmotaClient> _discovered = new(StringComparer.OrdinalIgnoreCase);
    private readonly TasmotaManagerOptions _opts;

    public TasmotaManager(IEnumerable<TasmotaDeviceConfig> configs, TasmotaManagerOptions opts)
    {
        _opts = opts;
        foreach (var c in configs)
            _explicit[c.Name] = Build(c);
        LoadCache();
    }

    private TasmotaClient Build(TasmotaDeviceConfig c) => new(c with
    {
        Password = c.Password ?? _opts.DefaultPassword,
        User = c.User ?? _opts.DefaultUser,
    });

    public IReadOnlyList<(string Name, string Host, string Source)> List()
    {
        var outp = new List<(string, string, string)>();
        foreach (var c in _explicit.Values) outp.Add((c.Name, c.Host, "config"));
        foreach (var c in _discovered.Values) outp.Add((c.Name, c.Host, "discovered"));
        return outp;
    }

    public TasmotaClient Get(string? nameOrHost)
    {
        if (string.IsNullOrEmpty(nameOrHost))
        {
            var all = _explicit.Values.Concat(_discovered.Values).ToList();
            if (all.Count == 1) return all[0];
            throw new InvalidOperationException("Multiple/zero devices — specify \"device\" (name or IP)");
        }
        if (_explicit.TryGetValue(nameOrHost, out var e)) return e;
        if (_discovered.TryGetValue(nameOrHost, out var d)) return d;
        foreach (var c in _explicit.Values.Concat(_discovered.Values))
            if (c.Host == nameOrHost) return c;
        if (System.Text.RegularExpressions.Regex.IsMatch(nameOrHost, "^[A-Za-z0-9.:_-]+$"))
            return Build(new TasmotaDeviceConfig(nameOrHost, nameOrHost));
        throw new InvalidOperationException($"Unknown Tasmota device: {nameOrHost}");
    }

    public async Task<string> DiscoverAsync(CancellationToken ct = default)
    {
        if (_opts.ScanTargets.Count == 0)
            throw new InvalidOperationException("No TASMOTA_SCAN subnets configured (e.g. \"192.168.4.0/24,192.168.10.0/24\")");
        var ips = ExpandTargets(_opts.ScanTargets);
        var found = new List<(string Ip, string Hostname)>();
        var started = DateTime.UtcNow;
        var idx = -1;
        var conc = Math.Min(_opts.ScanConcurrency, Math.Max(1, ips.Count));

        async Task Worker()
        {
            while (true)
            {
                var i = Interlocked.Increment(ref idx);
                if (i >= ips.Count) return;
                var r = await TasmotaClient.ProbeAsync(ips[i], _opts.DefaultUser, _opts.DefaultPassword, _opts.ScanTimeoutMs).ConfigureAwait(false);
                if (r is not null) lock (found) found.Add(r.Value);
            }
        }
        await Task.WhenAll(Enumerable.Range(0, conc).Select(_ => Worker())).ConfigureAwait(false);

        _discovered.Clear();
        foreach (var f in found.OrderBy(f => f.Ip, StringComparer.Ordinal))
            _discovered[string.IsNullOrEmpty(f.Hostname) ? f.Ip : f.Hostname] = Build(new TasmotaDeviceConfig(string.IsNullOrEmpty(f.Hostname) ? f.Ip : f.Hostname, f.Ip));
        SaveCache();

        var secs = (DateTime.UtcNow - started).TotalSeconds;
        var lines = found.Select(f => $"{(string.IsNullOrEmpty(f.Hostname) ? "(no name)" : f.Hostname)} — {f.Ip}");
        return string.Join('\n', new[]
        {
            $"Scanned {ips.Count} addresses across {string.Join(", ", _opts.ScanTargets)} in {secs:0.0}s (concurrency {conc}, timeout {_opts.ScanTimeoutMs}ms)",
            $"Found {found.Count} Tasmota device(s):",
        }.Concat(lines));
    }

    private void LoadCache()
    {
        if (_opts.CacheFile is null || !File.Exists(_opts.CacheFile)) return;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(_opts.CacheFile));
            if (doc.RootElement.TryGetProperty("devices", out var devs) && devs.ValueKind == JsonValueKind.Array)
                foreach (var d in devs.EnumerateArray())
                {
                    if (d.TryGetProperty("host", out var h) && h.ValueKind == JsonValueKind.String)
                    {
                        var host = h.GetString()!;
                        var name = d.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(n.GetString())
                            ? n.GetString()! : host;
                        _discovered[name] = Build(new TasmotaDeviceConfig(name, host));
                    }
                }
        }
        catch { /* ignore unreadable cache */ }
    }

    private void SaveCache()
    {
        if (_opts.CacheFile is null) return;
        try
        {
            var payload = new
            {
                scannedAt = DateTime.UtcNow.ToString("o"),
                devices = _discovered.Values.Select(c => new { name = c.Name, host = c.Host }).ToArray(),
            };
            File.WriteAllText(_opts.CacheFile, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"WARNING: could not write Tasmota cache ({_opts.CacheFile}): {e.Message}");
        }
    }

    /// <summary>Expand CIDR (/8-/32), ranges (a-b), or single IPs into host IPs, capped.</summary>
    public static List<string> ExpandTargets(IEnumerable<string> targets, int cap = 2048)
    {
        var ips = new List<string>();
        static uint ToInt(string ip) => ip.Split('.').Aggregate(0u, (a, o) => (a << 8) + (byte.Parse(o)));
        static string ToIp(uint n) => string.Join('.', new[] { 24, 16, 8, 0 }.Select(s => (n >> s) & 255));
        var ipRe = new System.Text.RegularExpressions.Regex(@"^\d+\.\d+\.\d+\.\d+$");

        foreach (var raw in targets)
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;
            if (t.Contains('/'))
            {
                var parts = t.Split('/');
                if (!ipRe.IsMatch(parts[0]) || !int.TryParse(parts[1], out var prefix) || prefix < 8 || prefix > 32) continue;
                var netInt = prefix == 0 ? 0u : ToInt(parts[0]) & (~0u << (32 - prefix));
                var count = prefix >= 31 ? (1u << (32 - prefix)) : (1u << (32 - prefix)) - 2;
                var start = prefix >= 31 ? netInt : netInt + 1;
                for (uint i = 0; i < count && ips.Count < cap; i++) ips.Add(ToIp(start + i));
            }
            else if (t.Contains('-'))
            {
                var parts = t.Split('-').Select(x => x.Trim()).ToArray();
                if (!ipRe.IsMatch(parts[0]) || !ipRe.IsMatch(parts[1])) continue;
                for (uint n = ToInt(parts[0]); n <= ToInt(parts[1]) && ips.Count < cap; n++) ips.Add(ToIp(n));
            }
            else if (ipRe.IsMatch(t))
            {
                ips.Add(t);
            }
        }
        return ips;
    }
}
