using System.Data;
using Miningcore.Persistence.Model;

namespace Miningcore.Persistence.Repositories;

/// <summary>
/// hashlak payout guard persistence (payout_guard.sql). Every write takes the caller's transaction.
/// </summary>
public interface IPayoutGuardRepository
{
    Task<PayoutGuardState> GetStateAsync(IDbConnection con, string poolId);

    /// <summary>Creates the pool's state row if missing and locks it (FOR UPDATE) for the rest of the transaction.</summary>
    Task<PayoutGuardState> GetStateForUpdateAsync(IDbConnection con, IDbTransaction tx, string poolId);

    /// <summary>Trips the breaker. An already tripped pool keeps its first reason and timestamp.</summary>
    Task TripAsync(IDbConnection con, IDbTransaction tx, string poolId, string reason);

    Task UpdateRunStatsAsync(IDbConnection con, IDbTransaction tx, string poolId, decimal paidToday, decimal expectedToday, decimal? walletBalance);

    /// <summary>Σ item amounts with status reserved/paid/unresolved whose run was created in the last 24 hours.</summary>
    Task<decimal> GetPaidLast24hAsync(IDbConnection con, IDbTransaction tx, string poolId);

    /// <summary>Σ positive balance_changes whose usage starts with "Reward for " in the last 24 hours (block reward credits).</summary>
    Task<decimal> GetCreditedLast24hAsync(IDbConnection con, IDbTransaction tx, string poolId);

    /// <summary>Number of distinct block heights credited ("Reward for … block H") in the last 24 hours.</summary>
    Task<int> GetCreditedBlocksLast24hAsync(IDbConnection con, IDbTransaction tx, string poolId);

    /// <summary>Current balance of an address, locked FOR UPDATE; null when there is no row.</summary>
    Task<decimal?> GetBalanceForUpdateAsync(IDbConnection con, IDbTransaction tx, string poolId, string address);

    Task<PayoutRun[]> GetOpenRunsAsync(IDbConnection con, string poolId);

    Task InsertRunAsync(IDbConnection con, IDbTransaction tx, PayoutRun run, PayoutRunItem[] items);

    /// <summary>Marks reserved items as paid. Returns the number of rows changed (the caller verifies it equals the request).</summary>
    Task<int> MarkItemsPaidAsync(IDbConnection con, IDbTransaction tx, Guid runId, IReadOnlyList<(string Address, string TxId, decimal SentAmount)> paid);

    Task MarkItemAsync(IDbConnection con, IDbTransaction tx, Guid runId, string address, string status, string error);

    Task<PayoutRunItem[]> GetItemsAsync(IDbConnection con, IDbTransaction tx, Guid runId);

    Task UpdateRunStatusAsync(IDbConnection con, IDbTransaction tx, Guid runId, string status, string error);
}
