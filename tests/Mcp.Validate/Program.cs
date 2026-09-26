using System.Text.Json;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// Live-validation harness: launch a built MCP server over stdio, list its tools,
// and optionally call read-only tools, printing the results.
//
//   Mcp.Validate <serverDll> [toolName argsJson] [toolName argsJson] ...
//
// Env vars for the target device are inherited from this process.

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: Mcp.Validate <serverDll> [toolName argsJson] ...");
    return 2;
}

var dll = args[0];
var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = Path.GetFileNameWithoutExtension(dll),
    Command = "dotnet",
    Arguments = new[] { dll },
});

await using var client = await McpClient.CreateAsync(transport);

var tools = await client.ListToolsAsync();
Console.WriteLine($"TOOLS ({tools.Count}):");
foreach (var t in tools.OrderBy(t => t.Name))
    Console.WriteLine($"  - {t.Name}");

// --parallel: fire all subsequent tool/args pairs concurrently (stress the server's
// per-connection serialisation). Otherwise calls run sequentially.
var parallel = args.Length > 1 && args[1] == "--parallel";

async Task CallOne(string toolName, string argsJson)
{
    Dictionary<string, object?> callArgs = new();
    if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
    {
        using var doc = JsonDocument.Parse(argsJson);
        foreach (var p in doc.RootElement.EnumerateObject())
            callArgs[p.Name] = ToObj(p.Value);
    }
    try
    {
        var result = await client.CallToolAsync(toolName, callArgs);
        Console.WriteLine($"[{toolName}] isError={result.IsError} :: {string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text.Replace('\n', ' '))).Trim()[..Math.Min(120, string.Join(" ", result.Content.OfType<TextContentBlock>().Select(t => t.Text.Replace('\n', ' '))).Trim().Length)]}");
    }
    catch (Exception e)
    {
        Console.WriteLine($"[{toolName}] EXCEPTION: {e.Message}");
    }
}

if (parallel)
{
    var pairs = new List<(string, string)>();
    for (var k = 2; k < args.Length; k += 2)
        pairs.Add((args[k], k + 1 < args.Length ? args[k + 1] : "{}"));
    // First pair runs sequentially (e.g. login to establish the session),
    // then the rest fire concurrently to stress serialisation.
    if (pairs.Count > 0)
    {
        await CallOne(pairs[0].Item1, pairs[0].Item2);
        var rest = pairs.Skip(1).ToList();
        Console.WriteLine($"\n=== {rest.Count} PARALLEL CALLS ===");
        await Task.WhenAll(rest.Select(p => CallOne(p.Item1, p.Item2)));
    }
    return 0;
}

var i = 1;
while (i + 1 < args.Length || i < args.Length)
{
    var toolName = args[i];
    var argsJson = i + 1 < args.Length ? args[i + 1] : "{}";
    i += 2;

    Dictionary<string, object?> callArgs = new();
    try
    {
        if (!string.IsNullOrWhiteSpace(argsJson) && argsJson != "{}")
        {
            using var doc = JsonDocument.Parse(argsJson);
            foreach (var p in doc.RootElement.EnumerateObject())
                callArgs[p.Name] = ToObj(p.Value);
        }
    }
    catch (Exception e)
    {
        Console.Error.WriteLine($"bad argsJson for {toolName}: {e.Message}");
        return 3;
    }

    Console.WriteLine($"\n=== CALL {toolName} {argsJson} ===");
    try
    {
        var result = await client.CallToolAsync(toolName, callArgs);
        Console.WriteLine($"isError: {result.IsError}");
        foreach (var block in result.Content)
            if (block is TextContentBlock text)
                Console.WriteLine(text.Text);
    }
    catch (Exception e)
    {
        Console.WriteLine($"EXCEPTION: {e.Message}");
    }
}

return 0;

static object? ToObj(JsonElement e) => e.ValueKind switch
{
    JsonValueKind.String => e.GetString(),
    JsonValueKind.Number => e.TryGetInt64(out var l) ? l : e.GetDouble(),
    JsonValueKind.True => true,
    JsonValueKind.False => false,
    JsonValueKind.Array => e.EnumerateArray().Select(ToObj).ToArray(),
    JsonValueKind.Object => e.EnumerateObject().ToDictionary(p => p.Name, p => ToObj(p.Value)),
    _ => null,
};
