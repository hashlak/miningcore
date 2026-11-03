using Miningcore.Blockchain.Xelis.DaemonRequests;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using Xunit;

namespace Miningcore.Tests.Blockchain;

public class XelisWalletRequestTests
{
    // the same settings AutofacModule registers for every RpcClient
    private static readonly JsonSerializerSettings settings = new()
    {
        ContractResolver = new DefaultContractResolver
        {
            NamingStrategy = new CamelCaseNamingStrategy { ProcessDictionaryKeys = false }
        }
    };

    [Fact]
    public void Fixed_Fee_Uses_The_Xelis_1_25_FeeBuilder_Shape()
    {
        // xelis_common FeeBuilder: {"fixed": u64}; the old {"value": …} is refused by xelis_wallet 1.25 (mainnet 2026-11-03)
        var request = new BuildTransactionRequest
        {
            Transfers = new[] { new BuildTransactionTransfer { Destination = "xel:dest", Asset = "asset", Amount = 60923946 } },
            Fee = new BuildTransactionFee { Fixed = 125000 }
        };

        var json = JObject.Parse(JsonConvert.SerializeObject(request, settings));

        Assert.Equal(125000UL, json["fee"]!["fixed"]!.Value<ulong>());
        Assert.Single(((JObject) json["fee"]!).Properties());
        Assert.Equal(60923946UL, json["transfers"]![0]!["amount"]!.Value<ulong>());
        Assert.True(json["broadcast"]!.Value<bool>());
    }

    [Fact]
    public void No_Fee_Means_The_Wallet_Computes_It()
    {
        var request = new BuildTransactionRequest
        {
            Transfers = new[] { new BuildTransactionTransfer { Destination = "xel:dest", Amount = 1 } }
        };

        var json = JObject.Parse(JsonConvert.SerializeObject(request, settings));

        Assert.Null(json["fee"]);
    }
}
