using System.Data;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Persistence.Model;

namespace Miningcore.Payments;

public interface IPayoutHandler
{
    Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct);

    Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct);
    Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, Block block, CancellationToken ct);
    Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct);
    double AdjustShareDifficulty(double difficulty);
    double AdjustBlockEffort(double effort);

    string FormatAmount(decimal amount);

    // hashlak payout guard (Payments/PayoutGuard.cs): the manager hands the reserved run to the handler before
    // PayoutAsync; the handler reports the hot wallet balance and rounds amounts the way its node will send them
    void BeginPayoutRun(PayoutRunContext run)
    {
    }

    Task<WalletBalance> GetWalletBalanceAsync(CancellationToken ct) => Task.FromResult<WalletBalance>(null);

    decimal NormalizePayoutAmount(decimal amount) => amount;
}

public interface IPayoutScheme
{
    Task UpdateBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, IPayoutHandler payoutHandler, Block block, decimal blockReward, CancellationToken ct);
}
