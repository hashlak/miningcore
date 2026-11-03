using AutoMapper;
using Miningcore.Api.Responses;
using Miningcore.Blockchain;
using Miningcore.Configuration;
using Miningcore.Mining;

namespace Miningcore.Api.Extensions;

public static class MiningPoolExtensions
{
    /// <summary>
    /// hashlak: the API serves no paymentProcessing extras at all. They are operational config — wallet passwords and
    /// private keys under family-specific names (upstream stripped walletPassword per family and missed Ravencoin,
    /// "progpow"), `_comment_*` notes — and nothing consumes them (the hashlak site reads the database). Security reviews
    /// 2026-11-03: a name denylist lets `WalletPass`, `pwd`, `xprv`, … through. AutoMapper copies Extra, so the live
    /// PoolConfig keeps its walletPassword (PayoutManager re-reads it every cycle).
    /// </summary>
    internal static void RedactPaymentProcessing(ApiPoolPaymentProcessingConfig paymentProcessing)
    {
        if(paymentProcessing != null)
            paymentProcessing.Extra = null;
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
        RedactPaymentProcessing(poolInfo.PaymentProcessing);

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
