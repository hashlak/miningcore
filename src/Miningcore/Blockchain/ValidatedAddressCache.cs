using Microsoft.Extensions.Caching.Memory;

namespace Miningcore.Blockchain;

/// <summary>
/// Addresses the daemon of ONE pool accepted, remembered so a reconnect storm (a farm after a power cut, a pool
/// restart) does not become hundreds of validateaddress RPCs per second. One instance per job manager, i.e. per
/// pool and per daemon: a testnet daemon's verdict never leaks into a mainnet pool. Bounded: at the size limit an
/// insert is refused until a background compaction frees ~5 % of the least recently used entries, and a miss simply
/// goes back to the daemon. Invalid addresses are never stored.
/// </summary>
public sealed class ValidatedAddressCache : IDisposable
{
    private readonly MemoryCache cache;
    private readonly TimeSpan ttl;

    public ValidatedAddressCache(int capacity = 50_000, TimeSpan? ttl = null)
    {
        cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = capacity });
        this.ttl = ttl ?? TimeSpan.FromHours(24);
    }

    public bool Contains(string address) => !string.IsNullOrEmpty(address) && cache.TryGetValue(address, out _);

    public void Remember(string address)
    {
        if(string.IsNullOrEmpty(address))
            return;

        cache.Set(address, true, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = ttl });
    }

    public int Count => cache.Count;

    public void Dispose() => cache.Dispose();
}
