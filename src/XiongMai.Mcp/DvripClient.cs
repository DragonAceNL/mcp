using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace XiongMai.Mcp;

/// <summary>
/// Client for the XiongMai "Sofia" DVRIP protocol (TCP/34567): 20-byte
/// little-endian header + JSON payload terminated with 0x0a 0x00.
/// </summary>
public sealed class DvripClient : IDisposable
{
    public static class Cmd
    {
        public const int Login = 1000, Logout = 1002, KeepAlive = 1006, SysInfo = 1020,
            ConfigGet = 1042, ChannelTitleGet = 1048, AbilityGet = 1360, OpMachine = 1450,
            OpTimeQuery = 1452, Snap = 1560, Users = 1472;
    }

    private static readonly Dictionary<int, string> RetCodes = new()
    {
        [100] = "OK", [101] = "Unknown error", [102] = "Version not supported", [103] = "Illegal request",
        [104] = "User already logged in", [105] = "User not logged in", [106] = "Username or password error",
        [107] = "Insufficient permission", [108] = "Timeout", [113] = "Command not supported",
        [203] = "Wrong password", [204] = "User does not exist", [205] = "Account locked (too many attempts)",
        [206] = "Account blacklisted",
    };

    public static string RetMessage(int? ret)
        => ret is null ? "no Ret field" : RetCodes.TryGetValue(ret.Value, out var m) ? m : $"code {ret}";

    /// <summary>XiongMai "Sofia" hash: MD5(password) folded into 8 chars of a 62-char alphabet.</summary>
    public static string SofiaHash(string password)
    {
        var md5 = MD5.HashData(Encoding.UTF8.GetBytes(password));
        const string chars = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        var sb = new StringBuilder(8);
        for (var i = 0; i < 8; i++)
        {
            var n = (md5[2 * i] + md5[2 * i + 1]) % 62;
            sb.Append(chars[n]);
        }
        return sb.ToString();
    }

    private readonly string _host;
    private readonly int _port;
    private readonly int _timeoutMs;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private uint _session;
    private uint _seq;

    public string SessionIdHex { get; private set; } = "0x0";
    public string? LoggedInUser { get; private set; }
    public bool Connected => _tcp is { Connected: true };

    public DvripClient(string host, int port = 34567, int timeoutMs = 6000)
    {
        _host = host;
        _port = port;
        _timeoutMs = timeoutMs;
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        _tcp = new TcpClient { ReceiveTimeout = _timeoutMs, SendTimeout = _timeoutMs };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_timeoutMs);
        await _tcp.ConnectAsync(_host, _port, cts.Token).ConfigureAwait(false);
        _stream = _tcp.GetStream();
    }

    public void Close()
    {
        try { _stream?.Dispose(); } catch { /* ignore */ }
        try { _tcp?.Dispose(); } catch { /* ignore */ }
        _stream = null;
        _tcp = null;
        _session = 0;
        _seq = 0;
        LoggedInUser = null;
        SessionIdHex = "0x0";
    }

    private byte[] BuildPacket(int msg, object payload)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(payload);
        var packet = new byte[20 + body.Length + 2];
        packet[0] = 0xff; // head flag
        packet[1] = 0x00; // version
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), _session);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), _seq);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), (ushort)msg);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16), (uint)(body.Length + 2));
        body.CopyTo(packet.AsSpan(20));
        packet[20 + body.Length] = 0x0a;
        packet[20 + body.Length + 1] = 0x00;
        return packet;
    }

    public async Task<(byte[] Raw, JsonElement? Json, int Msg)> RequestAsync(int msg, IDictionary<string, object?> payload, CancellationToken ct = default)
    {
        // Serialise requests: one socket, one in-flight request at a time.
        // Concurrent tool calls otherwise interleave writes/reads and corrupt framing.
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_stream is null) throw new InvalidOperationException("not connected");
            if (SessionIdHex != "0x0" && !payload.ContainsKey("SessionID"))
                payload = new Dictionary<string, object?>(payload) { ["SessionID"] = SessionIdHex };

            var packet = BuildPacket(msg, payload);
            _seq++;
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(_timeoutMs);
            await _stream.WriteAsync(packet, cts.Token).ConfigureAwait(false);

            // Read the 20-byte header first.
            var header = await ReadExactAsync(20, cts.Token).ConfigureAwait(false);
            var respMsg = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
            var expected = (int)BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16));
            var raw = expected > 0 ? await ReadExactAsync(expected, cts.Token).ConfigureAwait(false) : Array.Empty<byte>();

            var text = Encoding.UTF8.GetString(raw).TrimEnd('\x00', '\x0a');
            JsonElement? json = null;
            try { using var doc = JsonDocument.Parse(text); json = doc.RootElement.Clone(); } catch { json = null; }
            return (raw, json, respMsg);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<byte[]> ReadExactAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        var read = 0;
        while (read < count)
        {
            var n = await _stream!.ReadAsync(buf.AsMemory(read, count - read), ct).ConfigureAwait(false);
            if (n == 0) throw new IOException("connection closed while reading response");
            read += n;
        }
        return buf;
    }

    public async Task<(bool Ok, int? Ret, string Message, JsonElement? Response)> LoginAsync(string user, string password, CancellationToken ct = default)
    {
        if (!Connected) await ConnectAsync(ct).ConfigureAwait(false);
        var (_, json, _) = await RequestAsync(Cmd.Login, new Dictionary<string, object?>
        {
            ["EncryptType"] = "MD5",
            ["LoginType"] = "DVRIP-Web",
            ["UserName"] = user,
            ["PassWord"] = SofiaHash(password),
        }, ct).ConfigureAwait(false);

        int? ret = json is { } j && j.TryGetProperty("Ret", out var r) && r.ValueKind == JsonValueKind.Number ? r.GetInt32() : null;
        var ok = ret == 100;
        if (ok && json is { } j2)
        {
            SessionIdHex = j2.TryGetProperty("SessionID", out var sid) ? sid.GetString() ?? "0x0" : "0x0";
            _session = ParseHex(SessionIdHex);
            LoggedInUser = user;
        }
        return (ok, ret, RetMessage(ret), json);
    }

    private static uint ParseHex(string hex)
    {
        var s = hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? hex[2..] : hex;
        return uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var v) ? v : 0;
    }

    public async Task<JsonElement?> SysInfoAsync(string name = "SystemInfo", CancellationToken ct = default)
        => (await RequestAsync(Cmd.SysInfo, new Dictionary<string, object?> { ["Name"] = name }, ct)).Json;

    public async Task<JsonElement?> ConfigGetAsync(string name, CancellationToken ct = default)
        => (await RequestAsync(Cmd.ConfigGet, new Dictionary<string, object?> { ["Name"] = name }, ct)).Json;

    public async Task<JsonElement?> GetTimeAsync(CancellationToken ct = default)
        => (await RequestAsync(Cmd.OpTimeQuery, new Dictionary<string, object?> { ["Name"] = "OPTimeQuery" }, ct)).Json;

    public async Task<JsonElement?> GetUsersAsync(CancellationToken ct = default)
        => (await RequestAsync(Cmd.Users, new Dictionary<string, object?> { ["Name"] = "Users" }, ct)).Json;

    public async Task<JsonElement?> GetChannelTitlesAsync(CancellationToken ct = default)
        => (await RequestAsync(Cmd.ChannelTitleGet, new Dictionary<string, object?> { ["Name"] = "ChannelTitle" }, ct)).Json;

    public async Task<JsonElement?> RebootAsync(CancellationToken ct = default)
        => (await RequestAsync(Cmd.OpMachine, new Dictionary<string, object?>
        {
            ["Name"] = "OPMachine",
            ["OPMachine"] = new Dictionary<string, object?> { ["Action"] = "Reboot", ["Type"] = "Application" },
        }, ct)).Json;

    /// <summary>Best-effort JPEG snapshot. Fix X1: jpeg is null unless the FF D8 magic bytes are present.</summary>
    public async Task<(byte[]? Jpeg, JsonElement? Json)> SnapshotAsync(int channel = 0, CancellationToken ct = default)
    {
        var (raw, json, _) = await RequestAsync(Cmd.Snap, new Dictionary<string, object?>
        {
            ["Name"] = "OPSNAP",
            ["OPSNAP"] = new Dictionary<string, object?> { ["Channel"] = channel },
        }, ct).ConfigureAwait(false);
        if (json is not null) return (null, json);
        var isJpeg = raw.Length > 2 && raw[0] == 0xff && raw[1] == 0xd8;
        return (isJpeg ? raw : null, null);
    }

    public Task<(byte[] Raw, JsonElement? Json, int Msg)> CommandAsync(int msg, IDictionary<string, object?> payload, CancellationToken ct = default)
        => RequestAsync(msg, payload, ct);

    public void Dispose() => Close();
}
