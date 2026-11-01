namespace Miningcore.Blockchain;

/// <summary>Outcome of asking the coin daemon whether a miner's address is valid for its network.</summary>
public enum AddressValidation
{
    Valid,
    Invalid,
    /// <summary>The daemon did not answer (RPC error, timeout, queue full): not a verdict about the address.</summary>
    DaemonError,
}
