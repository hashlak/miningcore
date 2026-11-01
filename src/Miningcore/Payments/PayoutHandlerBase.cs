using System.Data;
using System.Data.Common;
using AutoMapper;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Time;
using Newtonsoft.Json;
using NLog;
using Polly;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Payments;

public abstract class PayoutHandlerBase
{
    protected PayoutHandlerBase(
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus)
    {
        Contract.RequiresNonNull(cf);
        Contract.RequiresNonNull(mapper);
        Contract.RequiresNonNull(shareRepo);
        Contract.RequiresNonNull(blockRepo);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(messageBus);

        this.cf = cf;
        this.mapper = mapper;
        this.clock = clock;
        this.shareRepo = shareRepo;
        this.blockRepo = blockRepo;
        this.balanceRepo = balanceRepo;
        this.paymentRepo = paymentRepo;
        this.messageBus = messageBus;

        BuildFaultHandlingPolicy();
    }

    protected readonly IBalanceRepository balanceRepo;
    protected readonly IBlockRepository blockRepo;
    protected readonly IConnectionFactory cf;
    protected readonly IMapper mapper;
    protected readonly IPaymentRepository paymentRepo;
    protected readonly IShareRepository shareRepo;
    protected readonly IMasterClock clock;
    protected readonly IMessageBus messageBus;
    protected ClusterConfig clusterConfig;
    private IAsyncPolicy faultPolicy;

    protected ILogger logger;
    protected PoolConfig poolConfig;
    private const int RetryCount = 8;

    /// <summary>hashlak payout guard: the reserved run for the PayoutAsync in progress (null = legacy, guard disabled)</summary>
    protected PayoutRunContext payoutRun;

    protected abstract string LogCategory { get; }

    public void BeginPayoutRun(PayoutRunContext run)
    {
        payoutRun = run;
    }

    public virtual Task<WalletBalance> GetWalletBalanceAsync(CancellationToken ct)
    {
        return Task.FromResult<WalletBalance>(null);
    }

    public virtual decimal NormalizePayoutAmount(decimal amount)
    {
        return amount;
    }

    /// <summary>
    /// hashlak payout guard: JSON-RPC error codes this handler's wallet raises BEFORE anything is signed or relayed
    /// (bad address, insufficient funds, locked wallet …). Only those let the guard re-credit a payee; every other
    /// error from a send is treated as "coins may have left". Default: none.
    /// </summary>
    protected virtual bool IsNotSentErrorCode(int rpcErrorCode)
    {
        return false;
    }

    /// <summary>
    /// hashlak payout guard: these payees are certainly NOT paid in this run (pre-check failed, page skipped, wallet locked …);
    /// the guard re-credits them when the run is finalized. Use NotifyPayoutFailure when the node was actually asked to send.
    /// </summary>
    protected void NotifyPayoutNotSent(Balance[] balances, string reason)
    {
        if(balances == null || balances.Length == 0)
            return;

        payoutRun?.Reject(balances, reason);

        logger.Info(() => $"[{LogCategory}] Not paying {balances.Length} address(es) this run: {reason}");
    }

    protected void BuildFaultHandlingPolicy()
    {
        var retry = Policy
            .Handle<DbException>()
            .Or<TimeoutException>()
            .WaitAndRetryAsync(RetryCount, retryAttempt => TimeSpan.FromSeconds(Math.Pow(2, retryAttempt)), OnRetry);

        faultPolicy = retry;
    }

    protected virtual void OnRetry(Exception ex, TimeSpan timeSpan, int retry, object context)
    {
        logger.Warn(() => $"[{LogCategory}] Retry {1} in {timeSpan} due to: {ex}");
    }

    public virtual async Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, Block block, CancellationToken ct)
    {
        var blockRewardRemaining = block.Reward;

        // Distribute funds to configured reward recipients
        foreach(var recipient in poolConfig.RewardRecipients.Where(x => x.Percentage > 0))
        {
            var amount = block.Reward * (recipient.Percentage / 100.0m);
            var address = recipient.Address;

            blockRewardRemaining -= amount;

            // skip transfers from pool wallet to pool wallet
            if(address != poolConfig.Address)
            {
                logger.Info(() => $"Crediting {address} with {FormatAmount(amount)}");
                await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, address, amount, $"Reward for block {block.BlockHeight}");
            }
        }

        return blockRewardRemaining;
    }

    protected async Task PersistPaymentsAsync(Balance[] balances, string transactionConfirmation)
    {
        Contract.RequiresNonNull(balances);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(transactionConfirmation));

        var coin = poolConfig.Template.As<CoinTemplate>();

        try
        {
            await faultPolicy.ExecuteAsync(async () =>
            {
                await cf.RunTx(async (con, tx) =>
                {
                    foreach(var balance in balances)
                    {
                        if(!string.IsNullOrEmpty(transactionConfirmation) && poolConfig.RewardRecipients.All(x => x.Address != balance.Address))
                        {
                            // record payment
                            var payment = new Payment
                            {
                                PoolId = poolConfig.Id,
                                Coin = coin.Symbol,
                                Address = balance.Address,
                                Amount = balance.Amount,
                                Created = clock.Now,
                                TransactionConfirmationData = transactionConfirmation
                            };

                            await paymentRepo.InsertAsync(con, tx, payment);
                        }

                        if(payoutRun == null)
                        {
                            // reset balance (legacy path: the guard is disabled for this pool)
                            logger.Info(() => $"[{LogCategory}] Resetting balance of {balance.Address}");
                            await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, balance.Address, -balance.Amount, "Balance reset after payment");
                        }
                    }

                    // hashlak payout guard: the balances were debited when the run was reserved — confirm the items instead
                    if(payoutRun != null)
                        await payoutRun.ConfirmAsync(con, tx, balances.Select(x => (x.Address, transactionConfirmation, x.Amount)).ToArray());
                });
            });
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"[{LogCategory}] Failed to persist the following payments: " +
                $"{JsonConvert.SerializeObject(balances.Where(x => x.Amount > 0).ToDictionary(x => x.Address, x => x.Amount))}");
            throw;
        }
    }

    protected async Task PersistPaymentsAsync(Dictionary<Balance, string> balances)
    {
        Contract.RequiresNonNull(balances);
        Contract.Requires<ArgumentException>(balances.Count > 0);

        var coin = poolConfig.Template.As<CoinTemplate>();

        try
        {
            await faultPolicy.ExecuteAsync(async () =>
            {
                await cf.RunTx(async (con, tx) =>
                {
                    foreach(var kvp in balances)
                    {
                        var (balance, transactionConfirmation) = kvp;

                        if(!string.IsNullOrEmpty(transactionConfirmation) && poolConfig.RewardRecipients.All(x => x.Address != balance.Address))
                        {
                            // record payment
                            var payment = new Payment
                            {
                                PoolId = poolConfig.Id,
                                Coin = coin.Symbol,
                                Address = balance.Address,
                                Amount = balance.Amount,
                                Created = clock.Now,
                                TransactionConfirmationData = transactionConfirmation
                            };

                            await paymentRepo.InsertAsync(con, tx, payment);
                        }

                        if(payoutRun == null)
                        {
                            // reset balance (legacy path: the guard is disabled for this pool)
                            logger.Info(() => $"[{LogCategory}] Resetting balance of {balance.Address}");
                            await balanceRepo.AddAmountAsync(con, tx, poolConfig.Id, balance.Address, -balance.Amount, "Balance reset after payment");
                        }
                    }

                    // hashlak payout guard: the balances were debited when the run was reserved — confirm the items instead
                    if(payoutRun != null)
                        await payoutRun.ConfirmAsync(con, tx, balances.Select(x => (x.Key.Address, x.Value, x.Key.Amount)).ToArray());
                });
            });
        }

        catch(Exception ex)
        {
            logger.Error(ex, () => $"[{LogCategory}] Failed to persist the following payments: " +
                $"{JsonConvert.SerializeObject(balances.Where(x => x.Key.Amount > 0).ToDictionary(x => x.Key.Address, x => x.Key.Amount))}");
            throw;
        }
    }

    public virtual double AdjustShareDifficulty(double difficulty)
    {
        return difficulty;
    }

    public string FormatAmount(decimal amount)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();
        return $"{amount:0.#####} {coin.Symbol}";
    }

    protected virtual void NotifyPayoutSuccess(string poolId, Balance[] balances, string[] txHashes, decimal? txFee)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();

        // admin notifications
        var explorerLinks = !string.IsNullOrEmpty(coin.ExplorerTxLink) ?
            txHashes.Select(x => string.Format(coin.ExplorerTxLink, x)).ToArray() :
            Array.Empty<string>();

        messageBus.SendMessage(new PaymentNotification(poolId, null, balances.Sum(x => x.Amount), coin.Symbol, balances.Length, txHashes, explorerLinks, txFee));
    }

    /// <summary>
    /// Reports a failed send. hashlak payout guard: <paramref name="rpcErrorCode"/> is the node's JSON-RPC error code when
    /// the node answered — an explicit error means the coins did not leave and the payees are re-credited; a transport
    /// error (-500), an exception or no code at all leaves them unresolved and trips the breaker.
    /// </summary>
    protected virtual void NotifyPayoutFailure(string poolId, Balance[] balances, string error, Exception ex, int? rpcErrorCode = null)
    {
        var coin = poolConfig.Template.As<CoinTemplate>();

        if(payoutRun != null && balances != null)
        {
            // a wallet rejection of a send is re-credited AND trips the breaker (a human looks before the next send)
            if(PayoutGuardPolicy.Classify(rpcErrorCode, ex, IsNotSentErrorCode) == PayoutFailureKind.Rejected)
                payoutRun.RejectBySend(balances, error ?? ex?.Message);
            else
                payoutRun.Unresolved(balances, error ?? ex?.Message);
        }

        messageBus.SendMessage(new PaymentNotification(poolId, error ?? ex?.Message, balances.Sum(x => x.Amount), coin.Symbol));
    }
}
