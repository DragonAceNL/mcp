using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mcp.Common.Mcp;

/// <summary>
/// Shared Host wiring for every MCP server: console logging is sent to stderr
/// only (stdout is the MCP stdio channel) and the MCP server + stdio transport
/// are registered. Each server then adds its own device-client singletons and
/// its tool type(s).
/// </summary>
public static class McpHost
{
    public static HostApplicationBuilder Create(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        builder.Logging.SetMinimumLevel(LogLevel.Information);
        return builder;
    }
}
