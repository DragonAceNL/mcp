using System.Text.RegularExpressions;
using ModelContextProtocol;

namespace Mcp.Common.Text;

/// <summary>
/// Regex allowlists that mirror the input guards in the original TS servers.
/// Interpolated shell/command values must pass these before use.
/// </summary>
public static partial class Validate
{
    [GeneratedRegex("^[A-Za-z0-9._:-]+$")] private static partial Regex HostRe();
    [GeneratedRegex("^[A-Za-z0-9._:-]+$")] private static partial Regex InterfaceRe();
    [GeneratedRegex(@"^[0-9.]+/[0-9]{1,2}$")] private static partial Regex CidrRe();
    [GeneratedRegex("^([0-9A-Fa-f]{2}:){5}[0-9A-Fa-f]{2}$")] private static partial Regex MacRe();
    [GeneratedRegex(@"^(\d{1,3}\.){3}\d{1,3}$")] private static partial Regex Ipv4Re();
    [GeneratedRegex("^[A-Za-z0-9_.:-]+$")] private static partial Regex NvramKeyRe();
    [GeneratedRegex("^(ssh-rsa|ssh-ed25519|ssh-dss|ecdsa-sha2-nistp256|ecdsa-sha2-nistp384|ecdsa-sha2-nistp521)$")] private static partial Regex SshKeyTypeRe();
    [GeneratedRegex("^[A-Za-z0-9+/=]+$")] private static partial Regex Base64BodyRe();
    [GeneratedRegex("^wg[0-9]+$")] private static partial Regex WgInterfaceRe();
    [GeneratedRegex("^[A-Za-z0-9._/-]+$")] private static partial Regex KeyPathRe();
    [GeneratedRegex("^[A-Za-z0-9._-]+$")] private static partial Regex UserNameRe();
    [GeneratedRegex("^[A-Za-z0-9._@-]+$")] private static partial Regex KeyIdRe();
    [GeneratedRegex(@"^[A-Za-z0-9._-]+:[0-9]{1,5}$")] private static partial Regex EndpointRe();
    [GeneratedRegex(@"^wl\d+(\.\d+)?$")] private static partial Regex WlInterfaceRe();
    [GeneratedRegex("^[a-z0-9_-]+$")] private static partial Regex ServiceTargetRe();
    [GeneratedRegex("^(restart|start|stop|reload)$")] private static partial Regex ServiceActionRe();
    [GeneratedRegex("^[A-Za-z0-9_-]+$")] private static partial Regex NetworkNameRe();
    [GeneratedRegex("^[A-Za-z0-9 ._()@$-]{1,80}$")] private static partial Regex WindowsServiceNameRe();
    [GeneratedRegex("^[A-Za-z0-9 /_.-]{1,128}$")] private static partial Regex EventLogNameRe();
    [GeneratedRegex(@"^[A-Za-z0-9._-]{1,64}$")] private static partial Regex SamAccountRe();

    public static bool IsHost(string v) => HostRe().IsMatch(v);
    public static bool IsInterface(string v) => InterfaceRe().IsMatch(v);
    public static bool IsCidr(string v) => CidrRe().IsMatch(v);
    public static bool IsMac(string v) => MacRe().IsMatch(v);
    public static bool IsIpv4(string v) => Ipv4Re().IsMatch(v);
    public static bool IsNvramKey(string v) => NvramKeyRe().IsMatch(v);
    public static bool IsSshKeyType(string v) => SshKeyTypeRe().IsMatch(v);
    public static bool IsBase64Body(string v) => Base64BodyRe().IsMatch(v);
    public static bool IsWgInterface(string v) => WgInterfaceRe().IsMatch(v);
    public static bool IsKeyPath(string v) => KeyPathRe().IsMatch(v);
    public static bool IsUserName(string v) => UserNameRe().IsMatch(v);
    public static bool IsKeyId(string v) => KeyIdRe().IsMatch(v);
    public static bool IsEndpoint(string v) => EndpointRe().IsMatch(v);
    public static bool IsWlInterface(string v) => WlInterfaceRe().IsMatch(v);
    public static bool IsServiceTarget(string v) => ServiceTargetRe().IsMatch(v);
    public static bool IsServiceAction(string v) => ServiceActionRe().IsMatch(v);
    public static bool IsNetworkName(string v) => NetworkNameRe().IsMatch(v);
    public static bool IsServiceName(string v) => WindowsServiceNameRe().IsMatch(v);
    public static bool IsEventLogName(string v) => EventLogNameRe().IsMatch(v);
    public static bool IsSamAccount(string v) => SamAccountRe().IsMatch(v);

    /// <summary>Throw an MCP-visible error if the value fails the predicate.</summary>
    public static string Require(string? value, Func<string, bool> ok, string message)
    {
        if (value is null || !ok(value))
            throw new McpException(message);
        return value;
    }

    /// <summary>Keep only characters in the safe set (used where the TS code sanitised rather than rejected).</summary>
    public static string Sanitize(string value, string extra = "")
    {
        var allowed = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_.:-" + extra;
        return new string(value.Where(c => allowed.IndexOf(c) >= 0).ToArray());
    }
}
