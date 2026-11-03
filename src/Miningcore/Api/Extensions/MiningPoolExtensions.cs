using AutoMapper;
using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Mining;

namespace Miningcore.Api.Extensions;

public static class MiningPoolExtensions
{
    // hashlak: any paymentProcessing key whose name contains one of these is never served by the API
    private static readonly string[] secretKeyParts = { "password", "passphrase", "privatekey", "secret", "mnemonic", "seed", "apikey", "token" };

    /// <summary>
    /// Removes every secret-looking key from a pool's paymentProcessing extras, whatever the coin family. Upstream
    /// stripped walletPassword per family and missed Ravencoin ("progpow"), so /api/pools served its wallet password.
    /// </summary>
    internal static void StripSecrets(IDictionary<string, object> extra)
    {
        if(extra == null)
            return;

        foreach(var key in extra.Keys.ToArray())
        {
            var name = key.ToLowerInvariant();

            if(secretKeyParts.Any(name.Contains))
                extra.Remove(key);
        }
    }

    public static PoolInfo ToPoolInfo(this PoolConfig poolConfig, IMapper mapper, Persistence.Model.PoolStats stats, IMiningPool pool)
    {
        var poolInfo = mapper.Map<PoolInfo>(poolConfig);

        poolInfo.PoolStats = mapper.Map<PoolStats>(stats);
        poolInfo.NetworkStats = pool?.NetworkStats ?? mapper.Map<BlockchainStats>(stats);

        // pool wallet link
        var addressInfobaseUrl = poolConfig.Template.ExplorerAccountLink;
        if(!string.IsNullOrEmpty(addressInfobaseUrl))
            poolInfo.AddressInfoLink = string.Format(addressInfobaseUrl, poolInfo.Address);

        // pool fees
        poolInfo.PoolFeePercent = poolConfig.RewardRecipients != null ? (float) poolConfig.RewardRecipients.Sum(x => x.Percentage) : 0;

        // strip security critical stuff
        if(poolInfo.PaymentProcessing != null)
            StripSecrets(poolInfo.PaymentProcessing.Extra);

        if(poolInfo.Ports != null)
        {
            foreach(var port in poolInfo.Ports.Keys)
            {
                var portInfo = poolInfo.Ports[port];

                portInfo.TlsPfxFile = null;
                portInfo.TlsPfxPassword = null;
            }
        }
        return poolInfo;
    }
}
