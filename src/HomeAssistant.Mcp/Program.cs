using HomeAssistant.Mcp;
using Mcp.Common.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = McpHost.Create(args);
builder.Services.AddSingleton(_ => HassClient.FromEnv());
builder.Services
    .AddMcpServer(o =>
    {
        o.ServerInfo = new() { Name = "homeassistant-mcp", Version = "0.1.0" };
    })
    .WithStdioServerTransport()
    .WithTools<HassTools>();

await builder.Build().RunAsync();
