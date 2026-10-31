using Newtonsoft.Json;

namespace Miningcore.Blockchain.Cryptonote.DaemonRequests;

/// <summary>
/// Daemon get_block request (by hash or height). The response carries the full block as JSON,
/// which is the only way to read the miner's own coinbase output on chains whose block
/// header "reward" field is not what the pool wallet receives (Zephyr).
/// </summary>
public class GetBlockRequest
{
    /// <summary>
    /// Block hash (preferred: ties the coinbase to the exact block whose orphan status was checked)
    /// </summary>
    [JsonProperty("hash", NullValueHandling = NullValueHandling.Ignore)]
    public string Hash { get; set; }

    [JsonProperty("height", NullValueHandling = NullValueHandling.Ignore)]
    public ulong? Height { get; set; }
}
