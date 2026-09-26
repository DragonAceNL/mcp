using Mcp.Common.Config;

namespace XiongMai.Mcp;

/// <summary>Default connection parameters from the environment, plus the reused DVRIP session.</summary>
public sealed class DvrState
{
    public string Host { get; } = EnvConfig.GetOr("DVR_HOST", "192.168.1.1");
    public int Port { get; } = EnvConfig.GetInt("DVR_PORT", 34567);
    public string User { get; } = EnvConfig.GetOr("DVR_USER", "admin");
    public string? Password { get; } = EnvConfig.Get("DVR_PASSWORD");

    public string TelnetUser { get; } = EnvConfig.GetOr("DVR_TELNET_USER", "root");
    public string TelnetPass { get; } = EnvConfig.GetOr("DVR_TELNET_PASS", "xc3511");
    public int TelnetPort { get; } = EnvConfig.GetInt("DVR_TELNET_PORT", 23);

    public DvripClient? Client { get; set; }
}
