using Newtonsoft.Json;

namespace Miningcore.Blockchain.Cryptonote.DaemonRequests;

/// <summary>
/// Wallet-RPC get_balance request. Multi-asset wallets (Zephyr, Salvium) return one entry per asset;
/// Zephyr's wallet-rpc only lists the default asset (ZPH) unless all_assets is set.
/// </summary>
public class GetBalanceRequest
{
    [JsonProperty("account_index")]
    public uint AccountIndex { get; set; }

    [JsonProperty("all_assets", NullValueHandling = NullValueHandling.Ignore)]
    public bool? AllAssets { get; set; }
}
