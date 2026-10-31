using Newtonsoft.Json;

namespace Miningcore.Blockchain.Cryptonote.DaemonResponses;

public class GetBlockResponse
{
    [JsonProperty("block_header")]
    public BlockHeader BlockHeader { get; set; }

    /// <summary>
    /// The block serialized as JSON (monerod/zephyrd "json" field): {"major_version":..,"miner_tx":{"vout":[{"amount":..,..}],..},..}
    /// </summary>
    public string Json { get; set; }

    [JsonProperty("miner_tx_hash")]
    public string MinerTxHash { get; set; }

    public string Status { get; set; }
}
