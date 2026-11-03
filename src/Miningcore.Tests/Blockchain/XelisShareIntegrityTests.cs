using Miningcore.Blockchain.Xelis;
using Miningcore.Extensions;
using Xunit;

namespace Miningcore.Tests.Blockchain;

public class XelisShareIntegrityTests
{
    [Fact]
    public void Nonce_Aliases_No_Longer_Pass()
    {
        // review 2026-11-03 (B1): both decode to the same 8 bytes, but were two different duplicate-check keys
        Assert.Equal("1a2b3c4d5e6f7081".HexToByteArray(), "0q1r2s3t4u5v6g7h".HexToByteArray());

        Assert.True(XelisJob.TryCanonicalNonce("1a2b3c4d5e6f7081", out var canonical));
        Assert.Equal("1a2b3c4d5e6f7081", canonical);
        Assert.False(XelisJob.TryCanonicalNonce("0q1r2s3t4u5v6g7h", out _));
    }

    [Fact]
    public void Upper_And_Lower_Case_Are_One_Nonce()
    {
        Assert.True(XelisJob.TryCanonicalNonce("1A2B3C4D5E6F7081", out var upper));
        Assert.True(XelisJob.TryCanonicalNonce("1a2b3c4d5e6f7081", out var lower));
        Assert.Equal(lower, upper);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1a2b3c4d5e6f708")]        // 15
    [InlineData("1a2b3c4d5e6f70811")]      // 17
    [InlineData("0x1a2b3c4d5e6f70")]       // prefix
    [InlineData("1a2b3c4d5e6f708 ")]       // whitespace
    [InlineData("1a2b3c4d5e6f708\n")]
    [InlineData("١a2b3c4d5e6f7081")]       // Arabic-Indic digit one: not ASCII hex
    [InlineData("1a2b3c4d5e6f708g")]
    public void Malformed_Nonces_Are_Rejected(string nonce)
    {
        Assert.False(XelisJob.TryCanonicalNonce(nonce, out var canonical));
        Assert.Null(canonical);
    }

    [Fact]
    public void Credited_Difficulty_Is_Capped_At_Network_Difficulty()
    {
        // review 2026-11-03 (B2): a static d=1e20 used to be credited 1e20 on a block candidate
        Assert.Equal(206836845d, XelisJob.CreditedDifficulty(1e20, 206836845));
        Assert.Equal(500000d, XelisJob.CreditedDifficulty(500000, 206836845));
        Assert.Equal(206836845d, XelisJob.CreditedDifficulty(206836845, 206836845));
    }
}
