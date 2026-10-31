namespace Miningcore.Blockchain.Cryptonote.DaemonRequests;

/// <summary>
/// Daemon get_block request (by height). The response carries the full block as JSON,
/// which is the only way to read the miner's own coinbase output on chains whose block
/// header "reward" field is not what the pool wallet receives (Zephyr).
/// </summary>
public class GetBlockRequest
{
    public ulong Height { get; set; }
}
