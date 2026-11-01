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
//   finalize — payees that certainly were not sent (pre-checks, or a wallet error known to come before signing) are
//              re-credited; anything else that is not confirmed is marked unresolved and TRIPS the breaker: coins may
//              or may not have left, a human decides (infra/host/payout-guard.sh resolve). A wallet rejection of a send
//              trips as well, after the re-credit, so a human looks before the next send. Nothing unresolved is ever
//              re-credited automatically.
//
// The breaker is a row in payout_guard_state; a restart never resets it.

/// <summary>Hot wallet balance as reported by a payout handler (unlocked = spendable now, null when the wallet has no such notion).</summary>
public record WalletBalance(decimal Total, decimal? Unlocked);

public enum PayoutFailureKind
{
    /// <summary>The wallet answered with an error it only raises before signing: the coins did not leave.</summary>
    Rejected,

    /// <summary>Transport error, timeout, exception, unknown error code or missing transaction id: the coins may have left.</summary>
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

    /// <summary>
    /// The daily cap is a rate limit, not a fault: items that fit into the remaining room (largest first) are paid now,
    /// the rest waits for the next window. Returns the items to pay and the deferred ones.
    /// </summary>
    public static (Balance[] Pay, Balance[] Deferred) ApplyDailyCap(IEnumerable<Balance> items, decimal paid24h, decimal dailyCap)
    {
        Contract.RequiresNonNull(items);

        var room = dailyCap - paid24h;
        var pay = new List<Balance>();
        var deferred = new List<Balance>();
        var sum = 0m;

        foreach(var item in items.OrderByDescending(x => x.Amount).ThenBy(x => x.Address, StringComparer.Ordinal))
        {
            if(room > 0 && sum + item.Amount <= room)
            {
                pay.Add(item);
                sum += item.Amount;
            }
            else
                deferred.Add(item);
        }

        return (pay.ToArray(), deferred.ToArray());
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

    /// <summary>
    /// Only an explicit error code the handler lists as "raised before anything is signed" means the coins stayed.
    /// A transport error (-500), an exception, no code at all, or any other code is unknown territory.
    /// </summary>
    public static PayoutFailureKind Classify(int? rpcErrorCode, Exception ex, Func<int, bool> isNotSentCode)
    {
        Contract.RequiresNonNull(isNotSentCode);

        if(ex == null && rpcErrorCode.HasValue && rpcErrorCode.Value != TransportErrorCode && isNotSentCode(rpcErrorCode.Value))
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
    private int sendRejections;

    /// <summary>The connection holding this pool's advisory payout lock from reservation until finalize (released there).</summary>
    public IDbConnection LockConnection { get; internal set; }

    public Guid RunId { get; }
    public string PoolId { get; }

    /// <summary>The capped amounts the handler must send — exactly what was debited.</summary>
    public Balance[] Balances { get; }

    public decimal Total => Balances.Sum(x => x.Amount);

    /// <summary>True when a wallet refused a send: finalize re-credits those payees and still trips the breaker.</summary>
    public bool HasSendRejection => sendRejections > 0;

    public string FirstSendRejection { get; private set; }

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

    /// <summary>A pre-check excluded these payees before any send was attempted: finalize re-credits them, no trip.</summary>
    public void Reject(IEnumerable<Balance> balances, string reason) => Transition(balances, PayoutItemState.Failed, reason);

    /// <summary>The wallet refused the send with a before-signing error: finalize re-credits them and trips the breaker.</summary>
    public void RejectBySend(IEnumerable<Balance> balances, string reason)
    {
        Transition(balances, PayoutItemState.Failed, reason);

        if(Interlocked.Increment(ref sendRejections) == 1)
            FirstSendRejection = PayoutGuardPolicy.Truncate(reason);
    }

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
    private readonly ConcurrentDictionary<string, int> lockMisses = new();
    private const int LockMissesBeforeWarning = 3;   // three 10-minute ticks

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

        if(cfg is { Enabled: false })
            return new PayoutGuardDecision.Legacy();

        var state = await cf.Run(con => repo.GetStateAsync(con, poolId));

        if(state?.Tripped == true)
        {
            // a run left reserved by a crash must become unresolved even when the pool was tripped by hand in between (quietly)
            await TripOnOpenRunAsync(poolId, "found while the pool was tripped", quiet: true);
            return Skip(poolId, $"breaker tripped since {state.TrippedAt:u}: {state.TrippedReason} — payout-guard.sh reset after checking");
        }

        if(cfg == null)
        {
            // fail closed: a pool without a guard block never pays (tripped once, not re-notified every tick)
            await TripAsync(poolId, "guard config missing (paymentProcessing.guard)");
            return Skip(poolId, "guard config missing");
        }

        if(await TripOnOpenRunAsync(poolId, "found before a payout run"))
            return Skip(poolId, "open run — payout-guard.sh resolve");

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

        // cap first, then round the way the node will send it, so a cap with more decimals than the coin never sends more than reserved
        var capped = PayoutGuardPolicy.ApplyPerPayoutCap(candidates, cfg.PerPayoutCap)
            .Select(x => x with { Amount = handler.NormalizePayoutAmount(x.Amount) })
            .Where(x => x.Amount > 0)
            .ToArray();

        if(capped.Length == 0)
            return Skip(poolId, "nothing payable after normalisation");

        // the wallet is asked outside the database transaction
        var wallet = await handler.GetWalletBalanceAsync(ct);

        if(wallet == null)
            return Skip(poolId, "wallet balance unavailable");

        var runId = Guid.NewGuid();
        var recipients = pool.RewardRecipients?.Select(x => x.Address).Where(x => !string.IsNullOrEmpty(x)).ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>();
        var coin = pool.Template.Symbol;
        string tripReason = null;
        string deferNote = null;

        // the pool's advisory payout lock lives on its own connection from here until finalize: a second process, or
        // the operator script, that cannot take it knows a run is in flight and leaves its rows alone
        var lockCon = await cf.OpenConnectionAsync();
        bool locked;

        try
        {
            locked = await repo.TryLockPoolAsync(lockCon, poolId);
        }

        catch
        {
            lockCon.Dispose();
            throw;
        }

        if(!locked)
        {
            lockCon.Dispose();

            // a lock nobody releases (a second Miningcore, a stuck psql session, a hung send in another process) would
            // otherwise only ever be an Info line: after a few ticks the owner is told (security review 2026-11-01, S6)
            var misses = lockMisses.AddOrUpdate(poolId, 1, (_, n) => n + 1);

            if(misses >= LockMissesBeforeWarning && ShouldLog("lockmiss:" + poolId))
                Warn(poolId, $"payout lock held by another session for {misses} ticks — nothing is paid or marked: check payout-guard.sh status (sending) and pg_stat_activity");

            return Skip(poolId, "another payout process holds this pool's lock");
        }

        lockMisses.TryRemove(poolId, out _);

        try
        {
            var decision = await cf.RunTx<PayoutGuardDecision>(async (con, tx) =>
            {
                var locked = await repo.GetStateForUpdateAsync(con, tx, poolId);

                if(locked.Tripped)
                    return new PayoutGuardDecision.Skip($"breaker tripped: {locked.TrippedReason}");

                // the one-open-run rule is enforced under the lock as well
                var open = await repo.GetOpenRunsAsync(con, tx, poolId);

                if(open.Length > 0)
                {
                    tripReason = $"open run {open[0].RunId} ({open[0].Status}) from {open[0].Created:u} — payout-guard.sh resolve";
                    await repo.MarkRunUnresolvedAsync(con, tx, open[0].RunId, "left open — found before a payout run");
                    await repo.TripAsync(con, tx, poolId, tripReason);
                    return new PayoutGuardDecision.Skip(tripReason);
                }

                var paid24h = await repo.GetPaidLast24hAsync(con, tx, poolId);
                var credited24h = await repo.GetCreditedLast24hAsync(con, tx, poolId);
                var blocks24h = await repo.GetCreditedBlocksLast24hAsync(con, tx, poolId);

                var anomaly = PayoutGuardPolicy.CreditAnomalyReasonOrNull(credited24h, blocks24h, cfg.NominalBlockReward, cfg.CreditMultiplier);

                if(anomaly != null)
                {
                    await repo.TripAsync(con, tx, poolId, anomaly);
                    await repo.UpdateRunStatsAsync(con, tx, poolId, paid24h, credited24h, wallet.Total);
                    tripReason = anomaly;
                    return new PayoutGuardDecision.Skip(anomaly);
                }

                // the daily cap defers, it does not trip: whatever fits into the remaining room is paid now
                var (pay, deferred) = PayoutGuardPolicy.ApplyDailyCap(capped, paid24h, cfg.DailyCap);

                if(deferred.Length > 0)
                    deferNote = $"daily cap: {deferred.Length} payee(s) ({deferred.Sum(x => x.Amount)}) deferred — {paid24h} paid in the last 24 h, cap {cfg.DailyCap}";

                if(pay.Length == 0)
                {
                    await repo.UpdateRunStatsAsync(con, tx, poolId, paid24h, credited24h, wallet.Total);
                    return new PayoutGuardDecision.Skip(deferNote ?? "daily cap reached");
                }

                var batch = pay.Sum(x => x.Amount);
                var walletReason = PayoutGuardPolicy.WalletTripReasonOrNull(wallet, batch);

                if(walletReason != null)
                {
                    await repo.TripAsync(con, tx, poolId, walletReason);
                    await repo.UpdateRunStatsAsync(con, tx, poolId, paid24h, credited24h, wallet.Total);
                    tripReason = walletReason;
                    return new PayoutGuardDecision.Skip(walletReason);
                }

                if(PayoutGuardPolicy.WalletUnlockedShort(wallet, batch))
                    return new PayoutGuardDecision.Skip($"unlocked wallet balance {wallet.Unlocked} below the batch {batch}");

                foreach(var balance in pay)
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
                    PayeeCount = pay.Length
                };

                var items = pay.Select(x => new PayoutRunItem
                {
                    RunId = runId,
                    Address = x.Address,
                    Amount = x.Amount,
                    Recipient = recipients.Contains(x.Address),
                    Status = PayoutRunStatus.Reserved
                }).ToArray();

                await repo.InsertRunAsync(con, tx, run, items);

                foreach(var balance in pay)
                    await balanceRepo.AddAmountAsync(con, tx, poolId, balance.Address, -balance.Amount, "Payout reserved", $"payout:{runId}");

                await repo.UpdateRunStatsAsync(con, tx, poolId, paid24h + batch, credited24h, wallet.Total);

                return new PayoutGuardDecision.Run(new PayoutRunContext(runId, poolId, pay, repo));
            });

            if(tripReason != null)
                Notify(poolId, tripReason);

            if(deferNote != null && ShouldLog($"{poolId}:defer"))
                logger.Warn(() => $"[{poolId}] {deferNote}");

            if(decision is PayoutGuardDecision.Run reserved)
            {
                reserved.Context.LockConnection = lockCon;   // released by FinalizeRunAsync
                lockCon = null;
                logger.Info(() => $"[{poolId}] Payout run {reserved.Context.RunId} reserved: {reserved.Context.Total} to {reserved.Context.Balances.Length} address(es)");
            }

            else if(decision is PayoutGuardDecision.Skip skip)
                return Skip(poolId, skip.Reason);

            return decision;
        }

        catch(BalanceChangedException ex)
        {
            // another writer touched a balance between the candidate query and the lock: nothing was reserved, next tick retries
            return Skip(poolId, ex.Message);
        }

        finally
        {
            if(lockCon != null)
                await ReleaseLockAsync(lockCon, poolId);
        }
    }

    private async Task ReleaseLockAsync(IDbConnection lockCon, string poolId)
    {
        try
        {
            await repo.UnlockPoolAsync(lockCon, poolId);
        }

        catch(Exception ex)
        {
            logger.Warn(() => $"[{poolId}] Could not release the payout lock explicitly ({ex.Message}); closing the connection releases it");
        }

        finally
        {
            lockCon.Dispose();
        }
    }

    public async Task FinalizeRunAsync(PayoutRunContext run, Exception handlerError, CancellationToken ct)
    {
        Contract.RequiresNonNull(run);

        if(!run.TryMarkFinalized())
            return;

        try
        {
            await FinalizeLockedAsync(run, handlerError);
        }

        finally
        {
            if(run.LockConnection != null)
            {
                await ReleaseLockAsync(run.LockConnection, run.PoolId);
                run.LockConnection = null;
            }
        }
    }

    private async Task FinalizeLockedAsync(PayoutRunContext run, Exception handlerError)
    {
        var snapshot = run.Snapshot();
        string tripReason = null;
        var paidCount = 0;
        var failedCount = 0;
        var unresolvedCount = 0;

        await cf.RunTx(async (con, tx) =>
        {
            // items are locked: the operator script cannot resolve them underneath this transaction
            var items = await repo.GetItemsForUpdateAsync(con, tx, run.RunId);

            foreach(var item in items)
            {
                if(item.Status == PayoutRunStatus.Paid)
                {
                    paidCount++;
                    continue;
                }

                if(item.Status != PayoutRunStatus.Reserved)
                {
                    // something settled this item while the run was in flight (a resolve on a live run): never touch its balance
                    tripReason ??= $"run {run.RunId}: item {item.Address} is {item.Status} although the run was still sending — resolved by hand during a live run?";
                    continue;
                }

                var memory = snapshot.TryGetValue(item.Address, out var known) ? known : (State: PayoutItemState.Reserved, Detail: null);

                if(memory.State == PayoutItemState.Failed)
                {
                    var detail = memory.Detail ?? "rejected before sending";

                    if(await repo.MarkReservedItemAsync(con, tx, run.RunId, item.Address, PayoutRunStatus.Failed, detail) != 1)
                    {
                        tripReason ??= $"run {run.RunId}: item {item.Address} changed underneath finalize";
                        continue;
                    }

                    await balanceRepo.AddAmountAsync(con, tx, run.PoolId, item.Address, item.Amount, $"Payout failed: {detail}", $"payout:{run.RunId}", "payout-recredit");
                    failedCount++;
                    continue;
                }

                // unresolved in memory, confirmed in memory but not committed, or never reported at all
                var reason = memory.State == PayoutItemState.Unresolved ?
                    memory.Detail :
                    PayoutGuardPolicy.Truncate(handlerError?.Message) ?? "handler returned without confirming the payment";

                await repo.MarkReservedItemAsync(con, tx, run.RunId, item.Address, PayoutRunStatus.Unresolved, reason);
                unresolvedCount++;
                tripReason ??= $"run {run.RunId} unresolved for {item.Address}: {reason}";
            }

            if(tripReason == null && run.HasSendRejection)
                tripReason = $"run {run.RunId}: the wallet refused a send ({run.FirstSendRejection}) — payees re-credited; check the wallet before the next run";

            var status = unresolvedCount > 0 ? PayoutRunStatus.Unresolved :
                paidCount > 0 ? PayoutRunStatus.Paid : PayoutRunStatus.Failed;

            var changed = await repo.UpdateReservedRunStatusAsync(con, tx, run.RunId, status, PayoutGuardPolicy.Truncate(tripReason ?? handlerError?.Message));

            if(changed != 1)
                tripReason ??= $"run {run.RunId} was no longer reserved when finalize ran — resolved by hand during a live run?";

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

            await TripOnOpenRunAsync(pool.Id, "found at startup");
        }
    }

    /// <summary>
    /// An open run outside a payout tick is the leftover of a crash: its reserved items become unresolved (the operator
    /// script only touches unresolved runs) and the pool trips. Returns true when a run was found.
    /// </summary>
    private async Task<bool> TripOnOpenRunAsync(string poolId, string where, bool quiet = false)
    {
        string reason = null;

        // a process that holds the pool's payout lock is sending right now: its reserved rows are live, not stale
        using var lockCon = await cf.OpenConnectionAsync();

        if(!await repo.TryLockPoolAsync(lockCon, poolId))
            return false;

        try
        {
            await MarkOpenRunsAsync(poolId, where, quiet, r => reason = r);
        }

        finally
        {
            try
            {
                await repo.UnlockPoolAsync(lockCon, poolId);
            }
            catch
            {
                // the connection is disposed right after: the session lock goes with it
            }
        }

        if(reason != null && !quiet)
            Notify(poolId, reason);
        else if(reason != null)
            logger.Warn(() => $"[{poolId}] {reason}");

        return reason != null;
    }

    private async Task MarkOpenRunsAsync(string poolId, string where, bool quiet, Action<string> setReason)
    {
        await cf.RunTx(async (con, tx) =>
        {
            await repo.GetStateForUpdateAsync(con, tx, poolId);
            var open = await repo.GetOpenRunsAsync(con, tx, poolId);

            if(open.Length == 0)
                return;

            var stale = open.Where(x => x.Status == PayoutRunStatus.Reserved).ToArray();

            foreach(var run in stale)
                await repo.MarkRunUnresolvedAsync(con, tx, run.RunId, $"left open — {where}");

            // on a pool that is already tripped only a newly marked run is news: an unresolved run waiting for the
            // operator is neither re-tripped nor logged every tick
            if(quiet && stale.Length == 0)
                return;

            var reason = $"open run {open[0].RunId} ({open[0].Status}) from {open[0].Created:u} {where} — payout-guard.sh resolve";
            await repo.TripAsync(con, tx, poolId, reason);
            setReason(reason);
        });
    }

    private async Task TripAsync(string poolId, string reason)
    {
        await cf.RunTx((con, tx) => repo.TripAsync(con, tx, poolId, reason));

        Notify(poolId, reason);
    }

    private void Warn(string poolId, string reason)
    {
        logger.Warn(() => $"[{poolId}] PAYOUT GUARD WARNING: {reason}");

        messageBus.SendMessage(new AdminNotification("Payout guard warning", $"[{poolId}] {reason}"));
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
