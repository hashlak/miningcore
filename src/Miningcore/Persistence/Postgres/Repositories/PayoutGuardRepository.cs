using System.Data;
using Dapper;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;

namespace Miningcore.Persistence.Postgres.Repositories;

/// <summary>
/// hashlak payout guard persistence. Columns are aliased explicitly because the guard tables use snake_case names
/// (payout_guard.sql) while Dapper matches property names without underscores here.
/// </summary>
public class PayoutGuardRepository : IPayoutGuardRepository
{
    private const string StateColumns = @"poolid AS PoolId, tripped AS Tripped, tripped_reason AS TrippedReason, tripped_at AS TrippedAt,
        day AS Day, paid_today AS PaidToday, expected_today AS ExpectedToday, wallet_balance AS WalletBalance,
        wallet_balance_at AS WalletBalanceAt, updated AS Updated";

    private const string RunColumns = @"run_id AS RunId, poolid AS PoolId, coin AS Coin, status AS Status, amount_total AS AmountTotal,
        payee_count AS PayeeCount, error AS Error, created AS Created, updated AS Updated";

    private const string ItemColumns = @"run_id AS RunId, address AS Address, amount AS Amount, sent_amount AS SentAmount,
        recipient AS Recipient, status AS Status, txid AS TxId, error AS Error, updated AS Updated";

    public Task<PayoutGuardState> GetStateAsync(IDbConnection con, string poolId)
    {
        var query = $"SELECT {StateColumns} FROM payout_guard_state WHERE poolid = @poolId";

        return con.QuerySingleOrDefaultAsync<PayoutGuardState>(query, new { poolId });
    }

    public async Task<PayoutGuardState> GetStateForUpdateAsync(IDbConnection con, IDbTransaction tx, string poolId)
    {
        const string ensure = @"INSERT INTO payout_guard_state(poolid, updated) VALUES(@poolId, now() at time zone 'utc')
            ON CONFLICT (poolid) DO NOTHING";

        await con.ExecuteAsync(ensure, new { poolId }, tx);

        var query = $"SELECT {StateColumns} FROM payout_guard_state WHERE poolid = @poolId FOR UPDATE";

        return await con.QuerySingleAsync<PayoutGuardState>(query, new { poolId }, tx);
    }

    public Task TripAsync(IDbConnection con, IDbTransaction tx, string poolId, string reason)
    {
        const string query = @"INSERT INTO payout_guard_state(poolid, tripped, tripped_reason, tripped_at, updated)
            VALUES(@poolId, true, @reason, now() at time zone 'utc', now() at time zone 'utc')
            ON CONFLICT (poolid) DO UPDATE SET
                tripped = true,
                tripped_reason = CASE WHEN payout_guard_state.tripped THEN payout_guard_state.tripped_reason ELSE EXCLUDED.tripped_reason END,
                tripped_at = CASE WHEN payout_guard_state.tripped THEN payout_guard_state.tripped_at ELSE EXCLUDED.tripped_at END,
                updated = now() at time zone 'utc'";

        return con.ExecuteAsync(query, new { poolId, reason }, tx);
    }

    public Task UpdateRunStatsAsync(IDbConnection con, IDbTransaction tx, string poolId, decimal paidToday, decimal expectedToday, decimal? walletBalance)
    {
        const string query = @"INSERT INTO payout_guard_state(poolid, day, paid_today, expected_today, wallet_balance, wallet_balance_at, updated)
            VALUES(@poolId, (now() at time zone 'utc')::date, @paidToday, @expectedToday, @walletBalance,
                   CASE WHEN @walletBalance IS NULL THEN NULL ELSE now() at time zone 'utc' END, now() at time zone 'utc')
            ON CONFLICT (poolid) DO UPDATE SET
                day = EXCLUDED.day,
                paid_today = EXCLUDED.paid_today,
                expected_today = EXCLUDED.expected_today,
                wallet_balance = COALESCE(EXCLUDED.wallet_balance, payout_guard_state.wallet_balance),
                wallet_balance_at = COALESCE(EXCLUDED.wallet_balance_at, payout_guard_state.wallet_balance_at),
                updated = EXCLUDED.updated";

        return con.ExecuteAsync(query, new { poolId, paidToday, expectedToday, walletBalance }, tx);
    }

    public Task<decimal> GetPaidLast24hAsync(IDbConnection con, IDbTransaction tx, string poolId)
    {
        const string query = @"SELECT COALESCE(SUM(i.amount), 0)
            FROM payout_run_items i JOIN payout_runs r ON r.run_id = i.run_id
            WHERE r.poolid = @poolId AND i.status IN ('reserved', 'paid', 'unresolved')
              AND r.created >= (now() at time zone 'utc') - interval '24 hours'";

        return con.ExecuteScalarAsync<decimal>(query, new { poolId }, tx);
    }

    public Task<decimal> GetCreditedLast24hAsync(IDbConnection con, IDbTransaction tx, string poolId)
    {
        const string query = @"SELECT COALESCE(SUM(amount), 0) FROM balance_changes
            WHERE poolid = @poolId AND amount > 0 AND usage LIKE 'Reward for %'
              AND created >= (now() at time zone 'utc') - interval '24 hours'";

        return con.ExecuteScalarAsync<decimal>(query, new { poolId }, tx);
    }

    public Task<int> GetCreditedBlocksLast24hAsync(IDbConnection con, IDbTransaction tx, string poolId)
    {
        // PPLNS/PROP credits read "Reward for N shares for block H", recipient credits "Reward for block H"
        const string query = @"SELECT COUNT(DISTINCT substring(usage from 'block ([0-9]+)$')) FROM balance_changes
            WHERE poolid = @poolId AND amount > 0 AND usage LIKE 'Reward for %'
              AND created >= (now() at time zone 'utc') - interval '24 hours'";

        return con.ExecuteScalarAsync<int>(query, new { poolId }, tx);
    }

    public Task<decimal?> GetBalanceForUpdateAsync(IDbConnection con, IDbTransaction tx, string poolId, string address)
    {
        const string query = "SELECT amount FROM balances WHERE poolid = @poolId AND address = @address FOR UPDATE";

        return con.QuerySingleOrDefaultAsync<decimal?>(query, new { poolId, address }, tx);
    }

    public async Task<PayoutRun[]> GetOpenRunsAsync(IDbConnection con, string poolId)
    {
        var query = $"SELECT {RunColumns} FROM payout_runs WHERE poolid = @poolId AND status IN ('reserved', 'unresolved') ORDER BY created";

        return (await con.QueryAsync<PayoutRun>(query, new { poolId })).ToArray();
    }

    public async Task InsertRunAsync(IDbConnection con, IDbTransaction tx, PayoutRun run, PayoutRunItem[] items)
    {
        const string runQuery = @"INSERT INTO payout_runs(run_id, poolid, coin, status, amount_total, payee_count, error, created, updated)
            VALUES(@RunId, @PoolId, @Coin, @Status, @AmountTotal, @PayeeCount, @Error, now() at time zone 'utc', now() at time zone 'utc')";

        await con.ExecuteAsync(runQuery, run, tx);

        const string itemQuery = @"INSERT INTO payout_run_items(run_id, address, amount, sent_amount, recipient, status, txid, error, updated)
            VALUES(@RunId, @Address, @Amount, @SentAmount, @Recipient, @Status, @TxId, @Error, now() at time zone 'utc')";

        foreach(var item in items)
            await con.ExecuteAsync(itemQuery, item, tx);
    }

    public async Task<int> MarkItemsPaidAsync(IDbConnection con, IDbTransaction tx, Guid runId, IReadOnlyList<(string Address, string TxId, decimal SentAmount)> paid)
    {
        const string query = @"UPDATE payout_run_items SET status = 'paid', txid = @txId, sent_amount = @sentAmount, updated = now() at time zone 'utc'
            WHERE run_id = @runId AND address = @address AND status = 'reserved'";

        var affected = 0;

        foreach(var (address, txId, sentAmount) in paid)
            affected += await con.ExecuteAsync(query, new { runId, address, txId, sentAmount }, tx);

        return affected;
    }

    public Task MarkItemAsync(IDbConnection con, IDbTransaction tx, Guid runId, string address, string status, string error)
    {
        const string query = @"UPDATE payout_run_items SET status = @status, error = @error, updated = now() at time zone 'utc'
            WHERE run_id = @runId AND address = @address";

        return con.ExecuteAsync(query, new { runId, address, status, error }, tx);
    }

    public async Task<PayoutRunItem[]> GetItemsAsync(IDbConnection con, IDbTransaction tx, Guid runId)
    {
        var query = $"SELECT {ItemColumns} FROM payout_run_items WHERE run_id = @runId ORDER BY address";

        return (await con.QueryAsync<PayoutRunItem>(query, new { runId }, tx)).ToArray();
    }

    public Task UpdateRunStatusAsync(IDbConnection con, IDbTransaction tx, Guid runId, string status, string error)
    {
        const string query = @"UPDATE payout_runs SET status = @status, error = @error, updated = now() at time zone 'utc' WHERE run_id = @runId";

        return con.ExecuteAsync(query, new { runId, status, error }, tx);
    }
}
