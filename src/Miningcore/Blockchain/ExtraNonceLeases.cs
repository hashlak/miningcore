using System.Reactive.Disposables;
using Miningcore.Mining;

namespace Miningcore.Blockchain;

/// <summary>
/// hashlak: an extranonce belongs to one live connection. The provider's counter wraps and hands out values still in
/// use; two connections on one extranonce see the same header on every job, so one rig's nonces are valid on the
/// other. A lease skips the values held by a live connection.
/// </summary>
public sealed class ExtraNonceLeases
{
    public ExtraNonceLeases(IExtraNonceProvider provider)
    {
        this.provider = provider;
    }

    private readonly IExtraNonceProvider provider;
    private readonly HashSet<string> held = new(StringComparer.Ordinal);

    /// <summary>
    /// False when every value is held. <paramref name="release"/> frees the value once, however often it is disposed.
    /// </summary>
    public bool TryLease(out string extraNonce, out IDisposable release)
    {
        extraNonce = null;
        release = null;

        lock(held)
        {
            // no lap of the counter when the answer is already known
            if((ulong) held.Count >= provider.Capacity)
                return false;

            string first = null;

            while(true)
            {
                var value = provider.Next();

                if(held.Add(value))
                {
                    extraNonce = value;
                    release = Disposable.Create(() => Release(value));
                    return true;
                }

                if(first == null)
                    first = value;
                else if(value == first)
                    return false;   // a full lap of the counter
            }
        }
    }

    private void Release(string value)
    {
        lock(held)
        {
            held.Remove(value);
        }
    }
}
