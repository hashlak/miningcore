using System;
using System.Linq;
using Miningcore.Payments;
using Miningcore.Persistence.Model;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PayoutGuardPolicyTests
{
    private static Balance B(string address, decimal amount) => new() { PoolId = "p", Address = address, Amount = amount };

    [Fact]
    public void PerPayoutCap_Truncates_Drops_Zero_And_Keeps_Order()
    {
        var capped = PayoutGuardPolicy.ApplyPerPayoutCap(new[] { B("a", 25), B("b", 10), B("c", 0), B("d", 3), B("e", -1) }, 10);

        Assert.Equal(new[] { "a", "b", "d" }, capped.Select(x => x.Address));
        Assert.Equal(new[] { 10m, 10m, 3m }, capped.Select(x => x.Amount));
        Assert.Equal(23m, capped.Sum(x => x.Amount));
    }

    [Fact]
    public void DailyCap_Is_Inclusive()
    {
        Assert.Null(PayoutGuardPolicy.DailyCapReasonOrNull(90, 10, 100));
        Assert.NotNull(PayoutGuardPolicy.DailyCapReasonOrNull(90, 10.000000000001m, 100));
        Assert.Contains("daily cap", PayoutGuardPolicy.DailyCapReasonOrNull(100, 1, 100));
    }

    [Fact]
    public void CreditAnomaly_Scales_With_Blocks_And_Multiplier()
    {
        Assert.Null(PayoutGuardPolicy.CreditAnomalyReasonOrNull(0, 0, 50, 1.2m));           // nothing credited: nothing to judge
        Assert.Null(PayoutGuardPolicy.CreditAnomalyReasonOrNull(60, 1, 50, 1.2m));          // exactly the allowance
        Assert.NotNull(PayoutGuardPolicy.CreditAnomalyReasonOrNull(60.5m, 1, 50, 1.2m));
        Assert.NotNull(PayoutGuardPolicy.CreditAnomalyReasonOrNull(1, 0, 50, 1.2m));        // credits without a credited block
        Assert.Null(PayoutGuardPolicy.CreditAnomalyReasonOrNull(1000, 20, 50, 1.2m));
        Assert.Contains("credit anomaly", PayoutGuardPolicy.CreditAnomalyReasonOrNull(500, 1, 50, 1.2m));
    }

    [Fact]
    public void Wallet_Total_Below_Batch_Trips_Unlocked_Short_Only_Skips()
    {
        Assert.NotNull(PayoutGuardPolicy.WalletTripReasonOrNull(new WalletBalance(9, 9), 10));
        Assert.Null(PayoutGuardPolicy.WalletTripReasonOrNull(new WalletBalance(10, 10), 10));

        Assert.True(PayoutGuardPolicy.WalletUnlockedShort(new WalletBalance(100, 5), 10));
        Assert.False(PayoutGuardPolicy.WalletUnlockedShort(new WalletBalance(100, 10), 10));
        Assert.False(PayoutGuardPolicy.WalletUnlockedShort(new WalletBalance(100, null), 10));
    }

    [Theory]
    [InlineData(-6, false, PayoutFailureKind.Rejected)]     // explicit daemon error: coins did not leave
    [InlineData(-4, false, PayoutFailureKind.Rejected)]
    [InlineData(-500, false, PayoutFailureKind.Unresolved)] // transport error / timeout: the node may have acted
    [InlineData(null, false, PayoutFailureKind.Unresolved)] // no code at all
    [InlineData(null, true, PayoutFailureKind.Unresolved)]
    [InlineData(-6, true, PayoutFailureKind.Unresolved)]    // an exception always wins
    public void Classify(int? code, bool withException, PayoutFailureKind expected)
    {
        Assert.Equal(expected, PayoutGuardPolicy.Classify(code, withException ? new Exception("boom") : null));
    }

    [Fact]
    public void Truncate_Keeps_Short_Text_And_Nulls()
    {
        Assert.Null(PayoutGuardPolicy.Truncate(null));
        Assert.Equal("abc", PayoutGuardPolicy.Truncate("abc"));
        Assert.Equal(500, PayoutGuardPolicy.Truncate(new string('x', 600)).Length);
    }
}
