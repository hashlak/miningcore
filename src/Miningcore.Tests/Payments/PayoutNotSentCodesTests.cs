using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Cryptonote;
using Xunit;

namespace Miningcore.Tests.Payments;

/// <summary>
/// The allow-lists decide whether a failed send is re-credited (certainly not sent) or left unresolved for a human.
/// A code wrongly on a list pays a miner twice; these tests pin the lists.
/// </summary>
public class PayoutNotSentCodesTests
{
    [Theory]
    [InlineData(-3)]
    [InlineData(-5)]
    [InlineData(-6)]
    [InlineData(-8)]
    [InlineData(-13)]
    [InlineData(-14)]
    [InlineData(-32602)]
    [InlineData(-32600)]
    public void Bitcoin_Codes_Raised_Before_Relay_Are_Not_Sent(int code)
    {
        Assert.True(BitcoinPayoutHandler.IsNotSentCode(code));
    }

    [Theory]
    [InlineData(-4)]      // "Transaction commit failed": the wallet may already have relayed
    [InlineData(-25)]     // rejected by the mempool after a broadcast attempt
    [InlineData(-26)]
    [InlineData(-500)]    // RpcClient timeout
    [InlineData(-1)]
    [InlineData(0)]
    public void Bitcoin_Other_Codes_Stay_Unresolved(int code)
    {
        Assert.False(BitcoinPayoutHandler.IsNotSentCode(code));
    }

    [Theory]
    [InlineData(-2)]
    [InlineData(-5)]
    [InlineData(-16)]
    [InlineData(-17)]
    [InlineData(-20)]
    public void Cryptonote_Codes_Raised_Before_A_Transaction_Exists_Are_Not_Sent(int code)
    {
        Assert.True(CryptonotePayoutHandler.IsNotSentCode(code));
    }

    [Theory]
    [InlineData(-3)]      // daemon busy
    [InlineData(-4)]      // generic transfer error, also "tx too big" and relay problems
    [InlineData(-18)]     // transaction not possible
    [InlineData(-38)]     // no connection to the daemon: the tx may already be in it
    [InlineData(-500)]
    [InlineData(0)]
    public void Cryptonote_Other_Codes_Stay_Unresolved(int code)
    {
        Assert.False(CryptonotePayoutHandler.IsNotSentCode(code));
    }
}
