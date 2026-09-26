using Mcp.Common.Config;
using Mcp.Common.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Synology.Mcp;

var baseUrl = EnvConfig.GetOr("SYNO_URL", "http://192.168.1.1:5000");
var user = EnvConfig.GetOr("SYNO_USER", "admin");
var passwordFile = EnvConfig.Get("SYNO_PASSWORD_FILE");
var inlinePassword = EnvConfig.Get("SYNO_PASSWORD");
var hasPassword = passwordFile is not null || inlinePassword is not null;

string LoadPassword()
{
    if (passwordFile is not null) return EnvConfig.ReadSecretFile(passwordFile);
    return inlinePassword ?? "";
}

var state = new SynologyState
{
    BaseUrl = baseUrl,
    User = user,
    HasPassword = hasPassword,
    Factory = () => new DsmClient(baseUrl, user, LoadPassword()),
};

var builder = McpHost.Create(args);
builder.Services.AddSingleton(state);
builder.Services
    .AddMcpServer(o => o.ServerInfo = new() { Name = "synology-mcp", Version = "0.1.0" })
    .WithStdioServerTransport()
    .WithTools<SynologyTools>();

await builder.Build().RunAsync();
