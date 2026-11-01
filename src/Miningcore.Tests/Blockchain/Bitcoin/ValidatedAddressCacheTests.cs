using System;
using Miningcore.Blockchain.Bitcoin;
using Xunit;

namespace Miningcore.Tests.Blockchain.Bitcoin;

public class ValidatedAddressCacheTests
{
    [Fact]
    public void Remembers_Only_What_It_Was_Told()
    {
        using var cache = new ValidatedAddressCache();

        Assert.False(cache.Contains("mib4FH9sizBpTveP2Shf3Rpxf4VqsWNcQC"));
        cache.Remember("mib4FH9sizBpTveP2Shf3Rpxf4VqsWNcQC");
        Assert.True(cache.Contains("mib4FH9sizBpTveP2Shf3Rpxf4VqsWNcQC"));
        Assert.False(cache.Contains("MIB4FH9SIZBPTVEP2SHF3RPXF4VQSWNCQC"));   // base58 is case-sensitive
        Assert.False(cache.Contains(""));
    }

    [Fact]
    public void Instances_Do_Not_Share_Entries()
    {
        using var testnet = new ValidatedAddressCache();
        using var mainnet = new ValidatedAddressCache();

        testnet.Remember("mib4FH9sizBpTveP2Shf3Rpxf4VqsWNcQC");

        Assert.True(testnet.Contains("mib4FH9sizBpTveP2Shf3Rpxf4VqsWNcQC"));
        Assert.False(mainnet.Contains("mib4FH9sizBpTveP2Shf3Rpxf4VqsWNcQC"));
    }

    [Fact]
    public void Is_Bounded_And_Fails_Closed()
    {
        using var cache = new ValidatedAddressCache(capacity: 2);

        cache.Remember("a");
        cache.Remember("b");
        cache.Remember("c");   // over capacity: not stored, the caller falls back to the daemon

        Assert.True(cache.Contains("a"));
        Assert.True(cache.Contains("b"));
        Assert.False(cache.Contains("c"));
        Assert.True(cache.Count <= 2);
    }

    [Fact]
    public void Entries_Expire()
    {
        using var cache = new ValidatedAddressCache(ttl: TimeSpan.FromMilliseconds(50));

        cache.Remember("a");
        Assert.True(cache.Contains("a"));
        System.Threading.Thread.Sleep(150);
        Assert.False(cache.Contains("a"));
    }
}
