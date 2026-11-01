using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Miningcore.Util;

/// <summary>
/// hashlak: miner-supplied strings (login, worker name) are logged and stored. A login that failed the address
/// pre-check can be 32 KB of anything, including CR/LF that forges log lines; a worker name goes into `shares.worker`,
/// the API, the site and the Telegram bot. Nothing of that leaves this class unfiltered.
/// </summary>
public static class LogSanitizer
{
    // \z, not $: in .NET "$" also matches before a final newline, so "rig1\n" would pass unchanged (security review 2026-11-01, S5)
    private static readonly Regex workerName = new(@"^[A-Za-z0-9_-]{1,32}\z", RegexOptions.Compiled);

    /// <summary>Printable ASCII letters, digits and . : _ - only, at most <paramref name="max"/> characters, "…" when cut.</summary>
    public static string Name(string value, int max = 80)
    {
        if(string.IsNullOrEmpty(value))
            return string.Empty;

        var chars = value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '_' or '-').Take(max + 1).ToArray();

        return chars.Length > max ? new string(chars, 0, max) + "…" : new string(chars);
    }

    /// <summary>
    /// A worker name is 1–32 of [A-Za-z0-9_-] and is kept verbatim. Any other non-empty name (Arabic, spaces, longer)
    /// becomes a stable "w-" + 8 hex of its SHA-1 so that rigs stay distinguishable (per-rig offline alerts, stats)
    /// without carrying the raw text anywhere. Empty stays empty (the share is credited to the address).
    /// </summary>
    public static string WorkerName(string value)
    {
        if(string.IsNullOrEmpty(value))
            return string.Empty;

        if(workerName.IsMatch(value))
            return value;

        return "w-" + Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(value)), 0, 4).ToLowerInvariant();
    }
}
