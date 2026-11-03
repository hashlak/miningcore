using System.Collections.Generic;
using System.Linq;
using Miningcore.Api.Extensions;
using Xunit;

namespace Miningcore.Tests.Api;

public class MiningPoolExtensionsTests
{
    [Fact]
    public void Strips_Every_Secret_Whatever_The_Family()
    {
        // what the ravencoin pool ("progpow" family) served on /api/pools before the fix, plus the other families' keys
        var extra = new Dictionary<string, object>
        {
            ["minersPayTxFees"] = true,
            ["walletPassword"] = "x",
            ["WalletPrivateKey"] = "x",
            ["walletPassphrase"] = "x",
            ["apiKey"] = "x",
            ["mnemonic"] = "x",
            ["interval"] = 600,
        };

        MiningPoolExtensions.StripSecrets(extra);

        Assert.Equal(new[] { "interval", "minersPayTxFees" }, extra.Keys.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Null_Extras_Are_Fine()
    {
        MiningPoolExtensions.StripSecrets(null);
    }
}
