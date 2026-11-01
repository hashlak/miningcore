using System.Collections.Concurrent;
using System.Data;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments;

// hashlak payout guard (hashlak repo: docs/LEDGER.md, ADR 2026-11-01).
//
// Miningcore's handlers broadcast first and persist afterwards, so a timeout, a crash between the two, or an empty
// transaction id pays the same balance again on the next tick. The guard turns every payout into a run that is
// written to the database BEFORE anything reaches a node:
//
//   reserve  — payout_runs + payout_run_items are inserted and every balance is debited in ONE transaction
//              (balance_changes tagged "payout:<run_id>"), after the caps, the breaker and the hot-wallet balance passed;
//   pay      — the handler sends; PersistPaymentsAsync confirms items (status paid + txid) instead of debiting again;
//   finalize — items the handler reported as rejected by the node are re-credited; anything else that is not
//              confirmed is marked unresolved and TRIPS the breaker: coins may or may not have left, a human decides
//              (infra/host/payout-guard.sh resolve). Nothing unresolved is ever re-credited automatically.
//
// The breaker is a row in payout_guard_state; a restart never resets it.

/// <summary>Hot wallet balance as reported by a payout handler (unlocked = spendable now, null when the wallet has no such notion).</summary>
public record WalletBalance(decimal Total, decimal? Unlocked);

public enum PayoutFailureKind
{
    /// <summary>The node answered with an explicit error: the coins did not leave.</summary>
    Rejected,

    /// <summary>Transport error, timeout, exception or missing transaction id: the coins may have left.</summary>
    Unresolved
}

public enum PayoutItemState
{
    Reserved,
    Paid,
    Failed,
    Unresolved
}

/// <summary>Pure decision rules of the guard (unit-tested without a database).</summary>
public static class PayoutGuardPolicy
{
    /// <summary>RpcClient reports timeouts and transport errors with this code: the node may have acted.</summary>
    public const int TransportErrorCode = -500;

    public static Balance[] ApplyPerPayoutCap(IEnumerable<Balance> candidates, decimal cap)
    {
        Contract.RequiresNonNull(candidates);

        return candidates
            .Where(x => x.Amount > 0)
            .Select(x => x.Amount > cap ? x with { Amount = cap } : x)
            .Where(x => x.Amount > 0)
            .ToArray();
    }

    public static string DailyCapReasonOrNull(decimal paid24h, decimal batch, decimal dailyCap)
    {
        return paid24h + batch > dailyCap ?
            $"daily cap: {paid24h} paid in the last 24 h + batch {batch} > {dailyCap}" :
            null;
    }

    /// <summary>
    /// More was credited in the last 24 h than the confirmed blocks can explain — the check that catches a block-reward
    /// bug before the money leaves. Credits without a single credited block are an anomaly too.
    /// </summary>
    public static string CreditAnomalyReasonOrNull(decimal credited24h, int blocks24h, decimal nominalBlockReward, decimal multiplier)
    {
        if(credited24h <= 0)
            return null;

        var allowed = blocks24h * nominalBlockReward * multiplier;

        return credited24h > allowed ?
            $"credit anomaly: {credited24h} credited for {blocks24h} block(s) in the last 24 h > {blocks24h} x {nominalBlockReward} x {multiplier}" :
            null;
    }

    public static string WalletTripReasonOrNull(WalletBalance wallet, decimal batch)
    {
        Contract.RequiresNonNull(wallet);

        return wallet.Total < batch ? $"hot wallet balance {wallet.Total} below the batch {batch}" : null;
    }

    public static bool WalletUnlockedShort(WalletBalance wallet, decimal batch)
    {
        Contract.RequiresNonNull(wallet);

        return wallet.Unlocked.HasValue && wallet.Unlocked.Value < batch;
    }

    public static PayoutFailureKind Classify(int? rpcErrorCode, Exception ex)
    {
        if(ex == null && rpcErrorCode.HasValue && rpcErrorCode.Value != TransportErrorCode)
            return PayoutFailureKind.Rejected;

        return PayoutFailureKind.Unresolved;
    }

    public static string Truncate(string text, int max = 500)
    {
        if(string.IsNullOrEmpty(text) || text.Length <= max)
            return text;

        return text[..max];
    }
}

/// <summary>
/// One reserved payout run, handed to the handler for the duration of PayoutAsync. Confirmations are written inside
/// the handler's own persistence transaction; rejections and unknowns stay in memory until FinalizeRunAsync.
/// </summary>
public sealed class PayoutRunContext
{
    public PayoutRunContext(Guid runId, string poolId, Balance[] balances, IPayoutGuardRepository repo)
    {
        Contract.RequiresNonNull(balances);
        Contract.RequiresNonNull(repo);

        RunId = runId;
        PoolId = poolId;
        Balances = balances;
        this.repo = repo;

        foreach(var balance in balances)
            items[balance.Address] = (PayoutItemState.Reserved, null);
    }

    private readonly IPayoutGuardRepository repo;
    private readonly ConcurrentDictionary<string, (PayoutItemState State, string Detail)> items = new(StringComparer.Ordinal);
    private int finalized;

    public Guid RunId { get; }
    public string PoolId { get; }

    /// <summary>The capped amounts the handler must send — exactly what was debited.</summary>
    public Balance[] Balances { get; }

    public decimal Total => Balances.Sum(x => x.Amount);

    /// <summary>
    /// Marks reserved items as paid with their transaction id, inside the caller's transaction. Re-running the same
    /// confirmation (the persistence retry policy) is accepted; confirming an address that was not reserved, was
    /// already settled, or has no transaction id throws — the caller's transaction rolls back and the item ends up unresolved.
    /// </summary>
    public async Task ConfirmAsync(IDbConnection con, IDbTransaction tx, IReadOnlyList<(string Address, string TxId, decimal SentAmount)> paid)
    {
        Contract.RequiresNonNull(paid);

        foreach(var (address, txId, _) in paid)
        {
            if(string.IsNullOrEmpty(txId))
                throw new InvalidOperationException($"payout run {RunId}: no transaction id for {address}");

            if(!items.TryGetValue(address, out var current))
                throw new InvalidOperationException($"payout run {RunId}: {address} was not reserved");

            if(current.State == PayoutItemState.Paid && current.Detail == txId)
                continue;

            if(current.State != PayoutItemState.Reserved)
                throw new InvalidOperationException($"payout run {RunId}: {address} is already {current.State}");
        }

        var affected = await repo.MarkItemsPaidAsync(con, tx, RunId, paid);

        if(affected != paid.Count)
            throw new InvalidOperationException($"payout run {RunId}: {affected} of {paid.Count} items were still reserved");

        foreach(var (address, txId, _) in paid)
            items[address] = (PayoutItemState.Paid, txId);
    }

    /// <summary>The node refused these payees explicitly: the coins did not leave, finalize re-credits them.</summary>
    public void Reject(IEnumerable<Balance> balances, string reason) => Transition(balances, PayoutItemState.Failed, reason);

    /// <summary>Unknown outcome for these payees: finalize marks them unresolved and trips the breaker.</summary>
    public void Unresolved(IEnumerable<Balance> balances, string reason) => Transition(balances, PayoutItemState.Unresolved, reason);

    public IReadOnlyDictionary<string, (PayoutItemState State, string Detail)> Snapshot()
    {
        return new Dictionary<string, (PayoutItemState State, string Detail)>(items, StringComparer.Ordinal);
    }

    internal bool TryMarkFinalized() => Interlocked.Exchange(ref finalized, 1) == 0;

    private void Transition(IEnumerable<Balance> balances, PayoutItemState target, string reason)
    {
        if(balances == null)
            return;

        foreach(var balance in balances)
        {
            // first verdict wins; addresses outside the run are ignored (they were never debited)
            if(items.TryGetValue(balance.Address, out var current) && current.State == PayoutItemState.Reserved)
                items.TryUpdate(balance.Address, (target, PayoutGuardPolicy.Truncate(reason)), current);
        }
    }
}

public abstract record PayoutGuardDecision
{
    /// <summary>guard.enabled = false: the handler runs exactly as upstream (debit after broadcast). Never on mainnet.</summary>
    public sealed record Legacy : PayoutGuardDecision;

    /// <summary>Nothing was reserved and nothing is paid this tick.</summary>
    public sealed record Skip(string Reason) : PayoutGuardDecision;

    /// <summary>A run was reserved; the handler must be called with Context.Balances and the run finalized afterwards.</summary>
    public sealed record Run(PayoutRunContext Context) : PayoutGuardDecision;
}

public interface IPayoutGuard
{
    Task<PayoutGuardDecision> PrepareRunAsync(PoolConfig pool, IPayoutHandler handler, Balance[] candidates, CancellationToken ct);
    Task FinalizeRunAsync(PayoutRunContext run, Exception handlerError, CancellationToken ct);
    Task RecoverAtStartupAsync(IEnumerable<PoolConfig> pools, CancellationToken ct);
}

public class PayoutGuard : IPayoutGuard
{
    public PayoutGuard(IConnectionFactory cf, IPayoutGuardRepository repo, IBalanceRepository balanceRepo, IMessageBus messageBus)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(repo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(messageBus);

        this.cf = cf;
        this.repo = repo;
        this.balanceRepo = balanceRepo;
        this.messageBus = messageBus;
    }

    private static readonly ILogger logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan skipLogInterval = TimeSpan.FromHours(1);

    private readonly IConnectionFactory cf;
    private readonly IPayoutGuardRepository repo;
    private readonly IBalanceRepository balanceRepo;
    private readonly IMessageBus messageBus;
    private readonly ConcurrentDictionary<string, DateTime> lastSkipLog = new();

    private sealed class BalanceChangedException : Exception
    {
        public BalanceChangedException(string message) : base(message)
        {
        }
    }

    public async Task<PayoutGuardDecision> PrepareRunAsync(PoolConfig pool, IPayoutHandler handler, Balance[] candidates, CancellationToken ct)
    {
        Contract.RequiresNonNull(pool);
        Contract.RequiresNonNull(handler);
        Contract.RequiresNonNull(candidates);

        var poolId = pool.Id;
        var cfg = pool.PaymentProcessing?.Guard;

        if(cfg == null)
        {
            // fail closed: a pool without a guard block never pays
            await TripAsync(poolId, "guard config missing (paymentProcessing.guard)");
            return Skip(poolId, "guard config missing");
        }

        if(!cfg.Enabled)
            return new PayoutGuardDecision.Legacy();

        var state = await cf.Run(con => repo.GetStateAsync(con, poolId));

        if(state?.Tripped == true)
            return Skip(poolId, $"breaker tripped since {state.TrippedAt:u}: {state.TrippedReason} — payout-guard.sh reset after checking");

        var openRuns = await cf.Run(con => repo.GetOpenRunsAsync(con, poolId));

        if(openRuns.Length > 0)
        {
            var open = openRuns[0];
            var reason = $"open run {open.RunId} ({open.Status}) from {open.Created:u} — payout-guard.sh resolve";

            await TripAsync(poolId, reason);
            return Skip(poolId, reason);
        }

        if(!cfg.PayoutsEnabled)
            return Skip(poolId, "payoutsEnabled = false");

        // the pool's own wallet address can carry a miner balance (someone mined to it): paying it is a transfer to self
        // that moves nothing and that some wallets refuse (Xelis: "Sender is receiver") — never reserve it
        var own = candidates.Where(x => string.Equals(x.Address, pool.Address, StringComparison.Ordinal)).ToArray();

        if(own.Length > 0)
        {
            if(ShouldLog($"{poolId}:own"))
                logger.Warn(() => $"[{poolId}] The pool wallet address holds a miner balance of {own.Sum(x => x.Amount)} — never paid out (transfer to self); clear it by hand if needed");

            candidates = candidates.Except(own).ToArray();

            if(candidates.Length == 0)
                return Skip(poolId, "only the pool wallet address is above the minimum");
        }

        var normalized = candidates.Select(x => x with { Amount = handler.NormalizePayoutAmount(x.Amount) });
        var capped = PayoutGuardPolicy.ApplyPerPayoutCap(normalized, cfg.PerPayoutCap);

        if(capped.Length == 0)
            return Skip(poolId, "nothing payable after normalisation");

        var batch = capped.Sum(x => x.Amount);

        // the wallet is asked outside the database transaction
        var wallet = await handler.GetWalletBalanceAsync(ct);

        if(wallet == null)
            return Skip(poolId, "wallet balance unavailable");

        var walletReason = PayoutGuardPolicy.WalletTripReasonOrNull(wallet, batch);

        if(walletReason != null)
        {
            await TripAsync(poolId, walletReason);
            return Skip(poolId, walletReason);
        }

        if(PayoutGuardPolicy.WalletUnlockedShort(wallet, batch))
            return Skip(poolId, $"unlocked wallet balance {wallet.Unlocked} below the batch {batch}");

        var runId = Guid.NewGuid();
        var recipients = pool.RewardRecipients?.Select(x => x.Address).Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var coin = pool.Template.Symbol;
        string tripReason = null;

        try
        {
            var decision = await cf.RunTx<PayoutGuardDecision>(async (con, tx) =>
            {
                var locked = await repo.GetStateForUpdateAsync(con, tx, poolId);

                if(locked.Tripped)
                    return new PayoutGuardDecision.Skip($"breaker tripped: {locked.TrippedReason}");

                var paid24h = await repo.GetPaidLast24hAsync(con, tx, poolId);
                var credited24h = await repo.GetCreditedLast24hAsync(con, tx, poolId);
                var blocks24h = await repo.GetCreditedBlocksLast24hAsync(con, tx, poolId);

                var reason = PayoutGuardPolicy.DailyCapReasonOrNull(paid24h, batch, cfg.DailyCap) ??
                    PayoutGuardPolicy.CreditAnomalyReasonOrNull(credited24h, blocks24h, cfg.NominalBlockReward, cfg.CreditMultiplier);

                if(reason != null)
                {
                    await repo.TripAsync(con, tx, poolId, reason);
                    await repo.UpdateRunStatsAsync(con, tx, poolId, paid24h, credited24h, wallet.Total);
                    tripReason = reason;
                    return new PayoutGuardDecision.Skip(reason);
                }

                foreach(var balance in capped)
                {
                    var current = await repo.GetBalanceForUpdateAsync(con, tx, poolId, balance.Address);

                    if(current == null || current.Value < balance.Amount)
                        throw new BalanceChangedException($"balance of {balance.Address} is {current?.ToString() ?? "gone"}, below the reserved {balance.Amount}");
                }

                var run = new PayoutRun
                {
                    RunId = runId,
                    PoolId = poolId,
                    Coin = coin,
                    Status = PayoutRunStatus.Reserved,
                    AmountTotal = batch,
                    PayeeCount = capped.Length
                };

                var items = capped.Select(x => new PayoutRunItem
                {
                    RunId = runId,
                    Address = x.Address,
                    Amount = x.Amount,
                    Recipient = recipients.Contains(x.Address),
                    Status = PayoutRunStatus.Reserved
                }).ToArray();

                await repo.InsertRunAsync(con, tx, run, items);

                foreach(var balance in capped)
                    await balanceRepo.AddAmountAsync(con, tx, poolId, balance.Address, -balance.Amount, "Payout reserved", $"payout:{runId}");

                await repo.UpdateRunStatsAsync(con, tx, poolId, paid24h + batch, credited24h, wallet.Total);

                return new PayoutGuardDecision.Run(new PayoutRunContext(runId, poolId, capped, repo));
            });

            if(tripReason != null)
                Notify(poolId, tripReason);

            if(decision is PayoutGuardDecision.Run reserved)
                logger.Info(() => $"[{poolId}] Payout run {reserved.Context.RunId} reserved: {batch} to {capped.Length} address(es)");

            return decision;
        }

        catch(BalanceChangedException ex)
        {
            // another writer touched a balance between the candidate query and the lock: nothing was reserved, next tick retries
            return Skip(poolId, ex.Message);
        }
    }

    public async Task FinalizeRunAsync(PayoutRunContext run, Exception handlerError, CancellationToken ct)
    {
        Contract.RequiresNonNull(run);

        if(!run.TryMarkFinalized())
            return;

        var snapshot = run.Snapshot();
        string tripReason = null;
        var paidCount = 0;
        var failedCount = 0;

        await cf.RunTx(async (con, tx) =>
        {
            var items = await repo.GetItemsAsync(con, tx, run.RunId);

            foreach(var item in items)
            {
                if(item.Status == PayoutRunStatus.Paid)
                {
                    paidCount++;
                    continue;
                }

                if(item.Status != PayoutRunStatus.Reserved)
                    continue;

                var memory = snapshot.TryGetValue(item.Address, out var known) ? known : (State: PayoutItemState.Reserved, Detail: null);

                if(memory.State == PayoutItemState.Failed)
                {
                    var detail = memory.Detail ?? "rejected by the node";

                    await repo.MarkItemAsync(con, tx, run.RunId, item.Address, PayoutRunStatus.Failed, detail);
                    await balanceRepo.AddAmountAsync(con, tx, run.PoolId, item.Address, item.Amount, $"Payout failed: {detail}", $"payout:{run.RunId}", "payout-recredit");
                    failedCount++;
                    continue;
                }

                // unresolved in memory, confirmed in memory but not committed, or never reported at all
                var reason = memory.State == PayoutItemState.Unresolved ?
                    memory.Detail :
                    PayoutGuardPolicy.Truncate(handlerError?.Message) ?? "handler returned without confirming the payment";

                await repo.MarkItemAsync(con, tx, run.RunId, item.Address, PayoutRunStatus.Unresolved, reason);
                tripReason ??= $"run {run.RunId} unresolved for {item.Address}: {reason}";
            }

            var status = tripReason != null ? PayoutRunStatus.Unresolved :
                paidCount > 0 ? PayoutRunStatus.Paid : PayoutRunStatus.Failed;

            await repo.UpdateRunStatusAsync(con, tx, run.RunId, status, PayoutGuardPolicy.Truncate(tripReason ?? handlerError?.Message));

            if(tripReason != null)
                await repo.TripAsync(con, tx, run.PoolId, tripReason);
        });

        if(tripReason != null)
            Notify(run.PoolId, tripReason);
        else
            logger.Info(() => $"[{run.PoolId}] Payout run {run.RunId} finalized: {paidCount} paid, {failedCount} re-credited");
    }

    public async Task RecoverAtStartupAsync(IEnumerable<PoolConfig> pools, CancellationToken ct)
    {
        Contract.RequiresNonNull(pools);

        foreach(var pool in pools)
        {
            if(pool.PaymentProcessing?.Guard is { Enabled: false })
                continue;

            var state = await cf.Run(con => repo.GetStateAsync(con, pool.Id));

            if(state?.Tripped == true)
                logger.Warn(() => $"[{pool.Id}] Payout breaker is tripped since {state.TrippedAt:u}: {state.TrippedReason} — a restart never resets it (payout-guard.sh reset)");

            var openRuns = await cf.Run(con => repo.GetOpenRunsAsync(con, pool.Id));

            if(openRuns.Length > 0)
            {
                var open = openRuns[0];
                await TripAsync(pool.Id, $"open run {open.RunId} ({open.Status}) found at startup — payout-guard.sh resolve");
            }
        }
    }

    private async Task TripAsync(string poolId, string reason)
    {
        await cf.RunTx((con, tx) => repo.TripAsync(con, tx, poolId, reason));

        Notify(poolId, reason);
    }

    private void Notify(string poolId, string reason)
    {
        logger.Error(() => $"[{poolId}] PAYOUT BREAKER TRIPPED: {reason}");

        messageBus.SendMessage(new AdminNotification("Payout guard tripped", $"[{poolId}] {reason}"));
    }

    private PayoutGuardDecision Skip(string poolId, string reason)
    {
        if(ShouldLog(poolId))
            logger.Info(() => $"[{poolId}] Payout skipped: {reason}");
        else
            logger.Debug(() => $"[{poolId}] Payout skipped: {reason}");

        return new PayoutGuardDecision.Skip(reason);
    }

    /// <summary>True once per hour per key: recurring conditions are logged, not spammed, every 10-minute tick.</summary>
    private bool ShouldLog(string key)
    {
        var now = DateTime.UtcNow;
        var last = lastSkipLog.GetOrAdd(key, DateTime.MinValue);

        return now - last >= skipLogInterval && lastSkipLog.TryUpdate(key, now, last);
    }
}
