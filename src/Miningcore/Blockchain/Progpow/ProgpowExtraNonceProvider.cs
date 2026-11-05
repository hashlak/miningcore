namespace Miningcore.Blockchain.Progpow;

public class FiroExtraNonceProvider : ExtraNonceProviderBase
{
    public FiroExtraNonceProvider(string poolId, byte? clusterInstanceId) : base(poolId, FiroConstants.ExtranoncePlaceHolderLength, clusterInstanceId)
    {
    }
}

public class RavencoinExtraNonceProvider : ExtraNonceProviderBase
{
    /// <summary>
    /// hashlak: no instance bits, so all 65,536 values are available to leases. We run one Miningcore per database.
    /// </summary>
    public RavencoinExtraNonceProvider(string poolId, byte? clusterInstanceId) : base(poolId, RavencoinConstants.ExtranoncePlaceHolderLength, null, 0)
    {
    }
}