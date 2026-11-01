using System.Linq;
using Miningcore.Configuration;
using Newtonsoft.Json;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PayoutGuardConfigTests
{
    [Fact]
    public void Missing_Block_Binds_Null()
    {
        var cfg = JsonConvert.DeserializeObject<PoolPaymentProcessingConfig>("{\"enabled\": true, \"minimumPayment\": 1}");

        Assert.Null(cfg.Guard);
    }

    [Fact]
    public void Malformed_Block_Fails_The_Load()
    {
        Assert.ThrowsAny<JsonException>(() =>
            JsonConvert.DeserializeObject<PoolPaymentProcessingConfig>("{\"guard\": {\"perPayoutCap\": \"abc\"}}"));
    }

    [Fact]
    public void Defaults()
    {
        var cfg = JsonConvert.DeserializeObject<PoolPaymentProcessingConfig>("{\"guard\": {\"perPayoutCap\": 5, \"dailyCap\": 50, \"nominalBlockReward\": 2}}");

        Assert.True(cfg.Guard.Enabled);
        Assert.True(cfg.Guard.PayoutsEnabled);
        Assert.Equal(1.2m, cfg.Guard.CreditMultiplier);
        Assert.Equal(5m, cfg.Guard.PerPayoutCap);
    }

    [Theory]
    [InlineData(0, 50, 2, 1.2, false)]      // perPayoutCap must be > 0
    [InlineData(5, 0, 2, 1.2, false)]       // dailyCap must be > 0
    [InlineData(60, 50, 2, 1.2, false)]     // dailyCap must be >= perPayoutCap
    [InlineData(5, 50, 0, 1.2, false)]      // nominalBlockReward must be > 0
    [InlineData(5, 50, 2, 0.9, false)]      // creditMultiplier must be >= 1
    [InlineData(5, 50, 2, 1.0, true)]
    [InlineData(50, 50, 2, 1.5, true)]
    public void Validator_Rules(decimal perPayoutCap, decimal dailyCap, decimal nominal, decimal multiplier, bool valid)
    {
        var cfg = new PayoutGuardConfig
        {
            PerPayoutCap = perPayoutCap,
            DailyCap = dailyCap,
            NominalBlockReward = nominal,
            CreditMultiplier = multiplier
        };

        var result = new PayoutGuardConfigValidator().Validate(cfg);

        Assert.Equal(valid, result.IsValid);
    }

    [Fact]
    public void Disabled_Block_Is_Not_Validated()
    {
        var result = new PayoutGuardConfigValidator().Validate(new PayoutGuardConfig { Enabled = false });

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors.Select(x => x.ErrorMessage));
    }
}
