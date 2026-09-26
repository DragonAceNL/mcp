using System.Text.Json;

namespace Mcp.Common.Config;

/// <summary>
/// Reads configuration from environment variables and secret files. Values are
/// never logged. Mirrors the env-var conventions of the original TS servers.
/// </summary>
public static class EnvConfig
{
    public static string? Get(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrEmpty(v) ? null : v;
    }

    public static string GetOr(string name, string fallback) => Get(name) ?? fallback;

    public static int GetInt(string name, int fallback)
        => int.TryParse(Get(name), out var n) ? n : fallback;

    /// <summary>Read and trim a secret from a file path; throws with a clear message on failure.</summary>
    public static string ReadSecretFile(string path)
    {
        try
        {
            return File.ReadAllText(path).Trim();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"cannot read secret file '{path}': {e.Message}", e);
        }
    }

    /// <summary>Parse a JSON-valued environment variable, or null if unset.</summary>
    public static T? ParseJson<T>(string name)
    {
        var raw = Get(name);
        if (raw is null) return default;
        try
        {
            return JsonSerializer.Deserialize<T>(raw, JsonOpts);
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"{name} is not valid JSON: {e.Message}", e);
        }
    }

    public static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Write a line to stderr (stdout is reserved for the MCP stdio channel).</summary>
    public static void Warn(string message) => Console.Error.WriteLine(message);

    /// <summary>Write an error to stderr and exit the process.</summary>
    public static void Fail(string message)
    {
        Console.Error.WriteLine($"ERROR: {message}");
        Environment.Exit(1);
    }
}
