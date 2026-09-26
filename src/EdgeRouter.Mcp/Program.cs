using EdgeRouter.Mcp;
using Mcp.Common.Config;
using Mcp.Common.Mcp;
using Mcp.Common.Ssh;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = EnvConfig.GetOr("EDGEROUTER_HOST", "192.168.1.1");
var port = EnvConfig.GetInt("EDGEROUTER_PORT", 22);
var user = EnvConfig.GetOr("EDGEROUTER_USER", "ubnt");
var password = EnvConfig.Get("EDGEROUTER_PASSWORD");
var keyPath = EnvConfig.Get("EDGEROUTER_PRIVATE_KEY_PATH");
var inlineKey = EnvConfig.Get("EDGEROUTER_PRIVATE_KEY");

if (password is null && keyPath is null && inlineKey is null)
    EnvConfig.Fail("Set EDGEROUTER_PASSWORD, EDGEROUTER_PRIVATE_KEY, or EDGEROUTER_PRIVATE_KEY_PATH");

var ssh = new SshCommandSession(new SshOptions
{
    Host = host,
    Port = port,
    Username = user,
    Password = password,
    PrivateKey = inlineKey,
    PrivateKeyPath = keyPath,
    StrictExit = true,
});

var builder = McpHost.Create(args);
builder.Services.AddSingleton(ssh);
builder.Services.AddSingleton<EdgeRouterClient>();
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "edgerouter-mcp", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<EdgeRouterTools>();

await builder.Build().RunAsync();
