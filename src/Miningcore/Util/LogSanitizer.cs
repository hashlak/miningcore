using System.Text.RegularExpressions;

namespace Miningcore.Util;

/// <summary>
/// hashlak: miner-supplied strings (login, worker name) are logged and stored. A login that failed the address
/// pre-check can be 32 KB of anything, including CR/LF that forges log lines; a worker name goes into `shares.worker`,
/// the API, the site and the Telegram bot. Nothing of that leaves this class unfiltered.
/// </summary>
public static class LogSanitizer
{
    private static readonly Regex workerName = new("^[A-Za-z0-9_-]{1,32}$", RegexOptions.Compiled);

    /// <summary>Printable ASCII letters, digits and . : _ - only, at most <paramref name="max"/> characters, "…" when cut.</summary>
    public static string Name(string value, int max = 80)
    {
        if(string.IsNullOrEmpty(value))
            return string.Empty;

        var chars = value.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or ':' or '_' or '-').Take(max + 1).ToArray();

        return chars.Length > max ? new string(chars, 0, max) + "…" : new string(chars);
    }

    /// <summary>A worker name is 1–32 of [A-Za-z0-9_-]; anything else becomes empty (the share is still credited to the address).</summary>
    public static string WorkerName(string value)
    {
        return !string.IsNullOrEmpty(value) && workerName.IsMatch(value) ? value : string.Empty;
    }
}
