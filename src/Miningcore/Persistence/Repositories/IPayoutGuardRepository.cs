using System.Data;
using Miningcore.Persistence.Model;

namespace Miningcore.Persistence.Repositories;

/// <summary>
/// hashlak payout guard persistence (payout_guard.sql). Every write takes the caller's transaction.
/// </summary>
public interface IPayoutGuardRepository
{
    Task<PayoutGuardState> GetStateAsync(IDbConnection con, string poolId);

    /// <summary>
    /// Session-level advisory lock per pool (pg_try_advisory_lock on hashtext('hashlak-payout:' || poolid)): held on a
    /// dedicated connection from reservation until finalize, so a second process — or the operator script — can tell
    /// a live run from a stale one. Returns false when another session holds it.
    /// </summary>
    Task<bool> TryLockPoolAsync(IDbConnection con, string poolId);

    Task UnlockPoolAsync(IDbConnection con, string poolId);

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

    /// <summary>Same inside a transaction (the guard repeats the check under the state-row lock).</summary>
    Task<PayoutRun[]> GetOpenRunsAsync(IDbConnection con, IDbTransaction tx, string poolId);

    /// <summary>
    /// A run still `reserved` when no run is in flight (startup, or a later tick) can only be the leftover of a crash:
    /// the run and its reserved items become `unresolved` so the operator script may act on them.
    /// </summary>
    Task MarkRunUnresolvedAsync(IDbConnection con, IDbTransaction tx, Guid runId, string reason);

    Task InsertRunAsync(IDbConnection con, IDbTransaction tx, PayoutRun run, PayoutRunItem[] items);

    /// <summary>Marks reserved items as paid. Returns the number of rows changed (the caller verifies it equals the request).</summary>
    Task<int> MarkItemsPaidAsync(IDbConnection con, IDbTransaction tx, Guid runId, IReadOnlyList<(string Address, string TxId, decimal SentAmount)> paid);

    /// <summary>Moves one item from `reserved` to the given status. Returns 0 when the item was no longer reserved.</summary>
    Task<int> MarkReservedItemAsync(IDbConnection con, IDbTransaction tx, Guid runId, string address, string status, string error);

    /// <summary>The run's items, locked FOR UPDATE for the rest of the transaction (finalize vs. the operator script).</summary>
    Task<PayoutRunItem[]> GetItemsForUpdateAsync(IDbConnection con, IDbTransaction tx, Guid runId);

    /// <summary>Moves the run from `reserved` to the given status. Returns 0 when the run was no longer reserved.</summary>
    Task<int> UpdateReservedRunStatusAsync(IDbConnection con, IDbTransaction tx, Guid runId, string status, string error);
}
