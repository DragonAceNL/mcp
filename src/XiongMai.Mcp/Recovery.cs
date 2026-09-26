using System.Text.RegularExpressions;

namespace XiongMai.Mcp;

/// <summary>
/// Owner-authorised password recovery for XiongMai DVRs. Accounts live in a JSON
/// file on the NAND config partition; each user's <c>Password</c> field holds the
/// Sofia hash. Recovery replaces the target hash and hard-reboots so the running
/// app cannot rewrite the file from its in-memory copy on a clean shutdown.
/// </summary>
public static class Recovery
{
    public const string AccountFile = "/mnt/mtd/Config/Account1";

    public sealed record ResetResult(
        bool Ok, string Account, string? OldHash = null, string? NewHash = null,
        string? Backup = null, bool? Verified = null, bool? Rebooting = null,
        string? Error = null, List<string>? Log = null);

    public static string? ExtractPasswordHash(string cfg, string account)
    {
        var nameIdx = cfg.IndexOf($"\"Name\"", StringComparison.Ordinal);
        var m = Regex.Match(cfg, $"\"Name\"\\s*:\\s*\"{Regex.Escape(account)}\"");
        if (!m.Success) return null;
        var slice = cfg[m.Index..];
        var pm = Regex.Match(slice, "\"Password\"\\s*:\\s*\"([^\"]*)\"");
        return pm.Success && pm.Groups[1].Value.Length > 0 ? pm.Groups[1].Value : null;
    }

    public static async Task<ResetResult> ResetAccountPasswordAsync(TelnetClient t, string account, string newPassword, CancellationToken ct = default)
    {
        var log = new List<string>();
        var newHash = DvripClient.SofiaHash(newPassword);

        var cfg = await t.ExecAsync($"cat {AccountFile}", ct: ct);
        if (!Regex.IsMatch(cfg, $"\"Name\"\\s*:\\s*\"{Regex.Escape(account)}\""))
            return new ResetResult(false, account, Error: $"account \"{account}\" not found in {AccountFile}", Log: log);

        var oldHash = ExtractPasswordHash(cfg, account);
        if (oldHash is null)
            return new ResetResult(false, account, Error: "current password hash not found (or empty)", Log: log);

        log.Add($"current hash for \"{account}\": {oldHash}");
        log.Add($"new hash: {newHash}");

        var backup = $"{AccountFile}.bak";
        await t.ExecAsync($"cp {AccountFile} {backup}", ct: ct);
        log.Add($"backed up {AccountFile} -> {backup}");

        var occurrences = cfg.Split($"\"{oldHash}\"").Length - 1;
        var flag = occurrences > 1 ? "g" : "";
        if (occurrences > 1) log.Add($"note: hash appears {occurrences}x; replacing all");
        await t.ExecAsync($"sed -i 's#{oldHash}#{newHash}#{flag}' {AccountFile}", ct: ct);

        var after = await t.ExecAsync($"cat {AccountFile}", ct: ct);
        var verified = after.Contains($"\"{newHash}\"");
        log.Add($"new hash present after edit: {verified}");
        if (!verified)
        {
            await t.ExecAsync($"cp {backup} {AccountFile}", ct: ct);
            return new ResetResult(false, account, oldHash, newHash, backup, false, Error: "verify failed; restored backup", Log: log);
        }

        await t.ExecAsync("sync", ct: ct);
        t.FireAndForget("reboot -f");
        log.Add("issued \"reboot -f\" (device will drop offline ~30-90s)");
        return new ResetResult(true, account, oldHash, newHash, backup, true, true, Log: log);
    }
}
