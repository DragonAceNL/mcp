using Mcp.Common.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using XiongMai.Mcp;

var builder = McpHost.Create(args);
builder.Services.AddSingleton<DvrState>();
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "xiongmai-dvr-mcp", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<DvrTools>();

await builder.Build().RunAsync();
