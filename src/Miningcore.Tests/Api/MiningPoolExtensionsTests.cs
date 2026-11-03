using System.Collections.Generic;
using AutoMapper;
using Miningcore.Api.Extensions;
using Miningcore.Api.Responses;
using Miningcore.Configuration;
using Xunit;

namespace Miningcore.Tests.Api;

public class MiningPoolExtensionsTests
{
    private static readonly IMapper mapper = new MapperConfiguration(cfg => cfg.AddProfile(new AutoMapperProfile())).CreateMapper();

    [Fact]
    public void Api_Serves_No_Extras_And_The_Live_Config_Keeps_Its_Password()
    {
        // what the ravencoin pool ("progpow" family) carried, plus names a denylist would have missed
        var source = new PoolPaymentProcessingConfig
        {
            Enabled = true,
            MinimumPayment = 20,
            Extra = new Dictionary<string, object>
            {
                ["minersPayTxFees"] = true,
                ["walletPassword"] = "x",
                ["WalletPass"] = "y",
                ["xprv"] = "z",
            },
        };

        var api = mapper.Map<ApiPoolPaymentProcessingConfig>(source);
        MiningPoolExtensions.RedactPaymentProcessing(api);

        Assert.Null(api.Extra);
        Assert.Equal(20m, api.MinimumPayment);
        Assert.Equal("x", source.Extra["walletPassword"]);   // PayoutManager re-reads the live config every cycle
    }

    [Fact]
    public void Null_Is_Fine()
    {
        MiningPoolExtensions.RedactPaymentProcessing(null);
    }
}
