namespace Miningcore.Mining;

/// <summary>
/// hashlak: a nonce has exactly one spelling before it reaches a duplicate check. Number and hex parsers forgive
/// whitespace and odd characters, so a check on the raw string can be passed many times with the same nonce.
/// </summary>
public static class NonceFormat
{
    /// <summary>
    /// True for exactly <paramref name="length"/> ASCII hex characters. The canonical form is lowercase.
    /// </summary>
    public static bool TryCanonicalHex(string nonce, int length, out string canonical)
    {
        canonical = null;

        if(nonce == null || nonce.Length != length || !nonce.All(char.IsAsciiHexDigit))
            return false;

        canonical = nonce.ToLowerInvariant();
        return true;
    }
}
