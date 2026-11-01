using Miningcore.Blockchain.Xelis;
using Xunit;

namespace Miningcore.Tests.Blockchain;

public class XelisAddressTests
{
    private const string Plain = "xet:8qyfte30hh69crf7zaufacs2vrw86s4e759qy9d2pqejsgf4avtsq5mvmk7";

    [Fact]
    public void Plain_Addresses_Need_No_Rpc()
    {
        Assert.Equal(63, Plain.Length);
        Assert.True(XelisJobManager.IsPlainAddress(Plain));
        Assert.True(XelisJobManager.IsPlainAddress("xel:" + Plain[4..]));
        Assert.False(XelisJobManager.IsPlainAddress(Plain + "extra"));          // integrated addresses are longer
        Assert.False(XelisJobManager.IsPlainAddress("xet:" + new string('b', 59)));   // 'b' is not a bech32 character
        Assert.False(XelisJobManager.IsPlainAddress(null));
    }

    [Fact]
    public void Pre_Check_Rejects_Junk_Before_Any_Rpc()
    {
        Assert.True(XelisJobManager.LooksLikeAddress(Plain));
        Assert.True(XelisJobManager.LooksLikeAddress(Plain + "qqqqqqqqqq"));     // integrated: longer, still one colon
        Assert.False(XelisJobManager.LooksLikeAddress("xet:short"));
        Assert.False(XelisJobManager.LooksLikeAddress("xet:a:b" + new string('q', 60)));
        Assert.False(XelisJobManager.LooksLikeAddress(Plain + "\r\n"));
        Assert.False(XelisJobManager.LooksLikeAddress(new string('x', 200)));
    }
}
