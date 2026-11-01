using System;
using System.Linq;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Xelis.Configuration;
using Miningcore.Blockchain.Xelis.DaemonRequests;
using Miningcore.Blockchain.Xelis.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Block = Miningcore.Persistence.Model.Block;
using Contract = Miningcore.Contracts.Contract;
using static Miningcore.Util.ActionUtils;

namespace Miningcore.Blockchain.Xelis;

[CoinFamily(CoinFamily.Xelis)]
public class XelisPayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public XelisPayoutHandler(
        IComponentContext ctx,
        IConnectionFactory cf,
        IMapper mapper,
        IShareRepository shareRepo,
        IBlockRepository blockRepo,
        IBalanceRepository balanceRepo,
        IPaymentRepository paymentRepo,
        IMasterClock clock,
        IMessageBus messageBus) :
        base(cf, mapper, shareRepo, blockRepo, balanceRepo, paymentRepo, clock, messageBus)
    {
        Contract.RequiresNonNull(ctx);
        Contract.RequiresNonNull(balanceRepo);
        Contract.RequiresNonNull(paymentRepo);

        this.ctx = ctx;
    }

    protected readonly IComponentContext ctx;
    private RpcClient rpcClient;
    private RpcClient rpcClientWallet;
    private string network;
    private XelisPaymentProcessingConfigExtra extraPoolPaymentProcessingConfig;
    private int minConfirmations;

    protected override string LogCategory => "Xelis Payout Handler";
    
    #region IPayoutHandler

    public virtual async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        extraPoolPaymentProcessingConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<XelisPaymentProcessingConfigExtra>();
    
        logger = LogUtil.GetPoolScopedLogger(typeof(XelisPayoutHandler), pc);

        // configure standard daemon
        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

        var daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .Select(x =>
            {
                if(string.IsNullOrEmpty(x.HttpPath))
                    x.HttpPath = XelisConstants.DaemonRpcLocation;

                return x;
            })
            .ToArray();

        rpcClient = new RpcClient(daemonEndpoints.First(), jsonSerializerSettings, messageBus, pc.Id);

        // configure wallet daemon
        var walletDaemonEndpoints = pc.Daemons
            .Where(x => x.Category?.ToLower() == XelisConstants.WalletDaemonCategory)
            .Select(x =>
            {
                if(string.IsNullOrEmpty(x.HttpPath))
                    x.HttpPath = XelisConstants.DaemonRpcLocation;

                return x;
            })
            .ToArray();

        rpcClientWallet = new RpcClient(walletDaemonEndpoints.First(), jsonSerializerSettings, messageBus, pc.Id);

        var info = await rpcClient.ExecuteAsync<GetChainInfoResponse>(logger, XelisCommands.GetChainInfo, ct);
        if(info.Error != null)
            throw new Exception($"'{XelisCommands.DaemonName}' returned error: {info.Error.Message} (Code {info.Error.Code})");

        network = info.Response.Network;

        // wallet daemon needs to be online
        var isOnline = await rpcClientWallet.ExecuteAsync<object>(logger, XelisWalletCommands.IsOnline, ct);
        if(isOnline.Error != null)
            throw new Exception($"'{XelisWalletCommands.IsOnline}': {isOnline.Error.Message} (Code {isOnline.Error.Code})");

        // wallet daemon is offline
        if(!(bool)isOnline.Response)
        {
            logger.Warn(() => $"[{LogCategory}] '{XelisWalletCommands.DaemonName}' is offline...");

            // xelis_wallet 1.25: set_online_mode REQUIRES daemon_address ("Invalid params: missing field `daemon_address`");
            // point the wallet at the pool's own daemon endpoint (hashlak 2026-10-31)
            var daemon = daemonEndpoints.First();
            var setOnlineModeRequest = new SetOnlineModeRequest
            {
                DaemonAddress = $"{(daemon.Ssl ? "https" : "http")}://{daemon.Host}:{daemon.Port}"
            };

            var setOnlineMode = await rpcClientWallet.ExecuteAsync<object>(logger, XelisWalletCommands.SetOnlineMode, ct, setOnlineModeRequest);
            if(setOnlineMode.Error != null)
                throw new Exception($"'{XelisWalletCommands.SetOnlineMode}': {setOnlineMode.Error.Message} (Code {setOnlineMode.Error.Code})");

            // wallet daemon is online
            if((bool)setOnlineMode.Response)
                logger.Info(() => $"[{LogCategory}] '{XelisWalletCommands.DaemonName}' is now online...");
        }

        minConfirmations = extraPoolPaymentProcessingConfig?.MinimumConfirmations ?? (network == "mainnet" ? 60 : 50);
    }

    public virtual async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        if(blocks.Length == 0)
            return blocks;

        var info = await rpcClient.ExecuteAsync<GetChainInfoResponse>(logger, XelisCommands.GetChainInfo, ct);
        if(info.Error != null)
        {
            logger.Warn(() => $"[{LogCategory}] '{XelisCommands.GetChainInfo}': {info.Error.Message} (Code {info.Error.Code})");
            return blocks;
        }

        var coin = poolConfig.Template.As<XelisCoinTemplate>();
        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            for(var j = 0; j < page.Length; j++)
            {
                var block = page[j];

                var getBlockByHashRequest = new GetBlockByHashRequest
                {
                    Hash = block.Hash
                };
                
                var response = await rpcClient.ExecuteAsync<GetBlockByHashResponse>(logger, XelisCommands.GetBlockByHash, ct, getBlockByHashRequest);
                if(response.Error != null)
                {
                    logger.Warn(() => $"[{LogCategory}] '{XelisCommands.GetBlockByHash}': {response.Error.Message} (Code {response.Error.Code})");

                    // we lost that battle
                    if(response.Error.Code == (int)XelisRPCErrorCode.RPC_INVALID_PARAMS)
                    {
                        result.Add(block);

                        block.Status = BlockStatus.Orphaned;
                        block.Reward = 0;

                        logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} classified as orphaned because it's not on chain");

                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }
                }

                // we lost that battle
                else if(response.Response.Miner != poolConfig.Address)
                {
                    result.Add(block);

                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;

                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [Type: {response.Response.BlockType}] classified as orphaned because another miner [{response.Response.Miner}] was rewarded");

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                }
                else
                {
                    logger.Info(() => $"[{LogCategory}] Block {block.BlockHeight} [Type: {response.Response.BlockType}] uses a custom minimum confirmations calculation [{minConfirmations}]");

                    block.ConfirmationProgress = Math.Min(1.0d, (double) (info.Response.TopoHeight - block.BlockHeight) / minConfirmations);

                    result.Add(block);

                    messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);

                    // matured and spendable?
                    if(block.ConfirmationProgress >= 1)
                    {
                        block.ConfirmationProgress = 1;

                        block.Reward = (decimal) response.Response.MinerReward / XelisConstants.SmallestUnit;

                        // security
                        if (block.Reward > 0)
                        {
                            block.Status = BlockStatus.Confirmed;

                            logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} [Type: {response.Response.BlockType}] worth {FormatAmount(block.Reward)}");
                        }
                        else
                        {
                            block.Status = BlockStatus.Orphaned;
                            block.Reward = 0;
                        }
                        
                        messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    }
                }
            }
        }

        return result.ToArray();
    }

    public virtual async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        // ensure we have enough peers
        var enoughPeers = await EnsureDaemonsSynchedAsync(ct);
        if(!enoughPeers)
        {
            NotifyPayoutNotSent(balances, "daemon not synced or no peers");
            return;
        }

        // build args
        var amounts = balances
            .Where(x => x.Amount > 0)
            .ToDictionary(x => x.Address, x => x.Amount);

        if(amounts.Count == 0)
            return;
        
        var coin = poolConfig.Template.As<XelisCoinTemplate>();

        var balancesTotal = amounts.Sum(x => x.Value);
        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balancesTotal)} to {balances.Length} addresses");

        // hashlak: build_transaction is all-or-nothing per page — one address the daemon rejects would block every
        // payee of that page forever. Hold the bad ones back (like the Bitcoin handler); a transport error is not a verdict.
        logger.Info(() => $"[{LogCategory}] Validating addresses...");
        var payable = new List<Balance>();

        foreach(var balance in balances.Where(x => x.Amount > 0))
        {
            var validateAddressRequest = new ValidateAddressRequest
            {
                Address = balance.Address
            };

            var validateAddress = await rpcClient.ExecuteAsync<ValidateAddressResponse>(logger, XelisCommands.ValidateAddress, ct, validateAddressRequest);

            if(validateAddress.Error?.Code == PayoutGuardPolicy.TransportErrorCode)
            {
                logger.Warn(() => $"[{LogCategory}] Payout round skipped: validate_address unavailable ({validateAddress.Error.Message})");
                NotifyPayoutNotSent(balances, $"validate_address unavailable: {validateAddress.Error.Message}");
                return;
            }

            if(validateAddress.Error != null || validateAddress.Response is { IsValid: false })
            {
                logger.Error(() => $"[{LogCategory}] Holding back payout of {FormatAmount(balance.Amount)} to {balance.Address}: address rejected by the daemon ({validateAddress.Error?.Message ?? "is_valid false"})");
                NotifyPayoutNotSent(new[] { balance }, "address rejected by the daemon");
                continue;
            }

            payable.Add(balance);
        }

        balances = payable.ToArray();

        if(balances.Length == 0)
            return;

        amounts = balances.ToDictionary(x => x.Address, x => x.Amount);
        balancesTotal = amounts.Sum(x => x.Value);

        var wallet = await ReadWalletBalanceAsync(ct);
        if(wallet == null)
        {
            NotifyPayoutNotSent(balances, "wallet balance unavailable");
            return;
        }

        var walletBalance = wallet.Total;
    
        logger.Info(() => $"[{LogCategory}] Current wallet balance - Total: [{FormatAmount(walletBalance)}]");

        // bail if balance does not satisfy payments
        if(walletBalance < balancesTotal)
        {
            logger.Warn(() => $"[{LogCategory}] Wallet balance currently short of {FormatAmount(balancesTotal - walletBalance)}. Will try again");
            NotifyPayoutNotSent(balances, "wallet balance short");
            return;
        }

        var pageSize = extraPoolPaymentProcessingConfig?.MaximumDestinationPerTransfer ?? XelisConstants.MaximumDestinationPerTransfer;
        var pageCount = (int) Math.Ceiling((double) balances.Length / pageSize);

        logger.Info(() => $"[{LogCategory}] Maximum of simultaneous destination address in a single transaction: {pageSize}");

        for(var i = 0; i < pageCount; i++)
        {
            logger.Info(() => $"[{LogCategory}] Processing batch {i + 1}/{pageCount}");

            var page = balances
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            var buildTransactionRequest = new BuildTransactionRequest
            {
                Transfers = page
                    .Where(x => x.Amount > 0)
                    .Select(x =>
                    {
                        return new BuildTransactionTransfer
                        {
                            Destination = x.Address,
                            Asset = XelisConstants.TransactionDefaultAsset,
                            Amount = (ulong) Math.Floor(x.Amount * XelisConstants.SmallestUnit)
                        };
                    }).ToArray()
            };

            if(extraPoolPaymentProcessingConfig?.KeepTransactionFees == true)
            {
                var estimateFeesRequest = new EstimateFeesRequest
                {
                    Transfers = buildTransactionRequest.Transfers
                };

                var estimateFeesResponse = await rpcClientWallet.ExecuteAsync<object>(logger, XelisWalletCommands.EstimateFees, ct, estimateFeesRequest);
                if(estimateFeesResponse.Error != null)
                {
                    logger.Warn(()=> $"[{LogCategory}] '{XelisWalletCommands.EstimateFees}': {estimateFeesResponse.Error.Message} (Code {estimateFeesResponse.Error.Code})");
                    NotifyPayoutNotSent(page, "estimate_fees failed");
                    continue;
                }

                var estimatedTransactionFees = Convert.ToDecimal(estimateFeesResponse.Response) / XelisConstants.SmallestUnit;
                logger.Info(() => $"[{LogCategory}] Estimated transaction fees: {FormatAmount(estimatedTransactionFees)}");

                logger.Debug(() => $"[{LogCategory}] Pool does not pay the transaction fee, so each address will have its payout deducted with [{FormatAmount(estimatedTransactionFees / page.Length)}]");

                buildTransactionRequest = new BuildTransactionRequest
                {
                    Transfers = page
                        .Where(x => x.Amount > 0)
                        .Select(x =>
                        {
                            return new BuildTransactionTransfer
                            {
                                Destination = x.Address,
                                Asset = XelisConstants.TransactionDefaultAsset,
                                Amount = (ulong) Math.Floor((x.Amount > (estimatedTransactionFees / page.Length) ? x.Amount - (estimatedTransactionFees / page.Length) : x.Amount) * XelisConstants.SmallestUnit)
                            };
                        }).ToArray()
                };

                buildTransactionRequest.Fee = new BuildTransactionFee
                {
                    Amount = (ulong) (estimatedTransactionFees * XelisConstants.SmallestUnit)
                };
            }

            var buildTransactionResponse = await rpcClientWallet.ExecuteAsync<BuildTransactionResponse>(logger, XelisWalletCommands.BuildTransaction, ct, buildTransactionRequest);
            if(buildTransactionResponse.Error != null)
            {
                logger.Error(()=> $"[{LogCategory}] '{XelisWalletCommands.BuildTransaction}': {buildTransactionResponse.Error.Message} (Code {buildTransactionResponse.Error.Code})");
                NotifyPayoutFailure(poolConfig.Id, page, $"Daemon command '{XelisWalletCommands.BuildTransaction}' returned error: {buildTransactionResponse.Error.Message} code {buildTransactionResponse.Error.Code}", null, buildTransactionResponse.Error.Code);
                continue;
            }

            if(string.IsNullOrEmpty(buildTransactionResponse.Response?.Hash))
            {
                // build_transaction broadcasts by default: without a hash the outcome is unknown, never "not sent"
                logger.Error(() => $"[{LogCategory}] Payment transaction failed to return a transaction id");
                NotifyPayoutFailure(poolConfig.Id, page, $"Daemon command '{XelisWalletCommands.BuildTransaction}' returned no transaction hash", null);
                continue;
            }
            else
            {
                // payment successful
                var finalTransactionFees = (decimal) buildTransactionResponse.Response.Fee / XelisConstants.SmallestUnit;

                logger.Info(() => $"[{LogCategory}] Payment transaction id: {buildTransactionResponse.Response.Hash} || Payment transaction fees: {FormatAmount(finalTransactionFees)}");

                await PersistPaymentsAsync(page, buildTransactionResponse.Response.Hash);
                NotifyPayoutSuccess(poolConfig.Id, page, new[] { buildTransactionResponse.Response.Hash }, finalTransactionFees);
            }
        }
    }

    public double AdjustBlockEffort(double effort)
    {
        return effort;
    }

    // hashlak payout guard: the wallet's total balance (xelis_wallet has no locked/unlocked split); null = RPC error
    public override Task<WalletBalance> GetWalletBalanceAsync(CancellationToken ct)
    {
        return ReadWalletBalanceAsync(ct);
    }

    // build_transaction receives Math.Floor(amount × SmallestUnit): reserve exactly that (no dust between reserved and sent)
    public override decimal NormalizePayoutAmount(decimal amount)
    {
        return Math.Floor(amount * XelisConstants.SmallestUnit) / XelisConstants.SmallestUnit;
    }

    // xelis_wallet's build_transaction broadcasts by default and its error codes are not documented as pre-broadcast:
    // no code is ever treated as "not sent" (the base default) — a refused page trips the breaker for a human look

    #endregion // IPayoutHandler

    private async Task<WalletBalance> ReadWalletBalanceAsync(CancellationToken ct)
    {
        var responseBalance = await rpcClientWallet.ExecuteAsync<object>(logger, XelisWalletCommands.GetBalance, ct);

        if(responseBalance.Error != null || responseBalance.Response == null)
        {
            logger.Warn(() => $"[{LogCategory}] '{XelisWalletCommands.GetBalance}': {responseBalance.Error?.Message ?? "no response"} (Code {responseBalance.Error?.Code})");
            return null;
        }

        var total = Convert.ToDecimal(responseBalance.Response) / XelisConstants.SmallestUnit;

        return new WalletBalance(total, total);
    }

    private async Task<bool> EnsureDaemonsSynchedAsync(CancellationToken ct)
    {
        // ensure we have enough peers
        var status = await rpcClient.ExecuteAsync<GetStatusResponse>(logger, XelisCommands.GetStatus, ct);
        if(status.Error != null)
        {
            logger.Warn(() => $"'{XelisCommands.GetStatus}': {status.Error.Message} (Code {status.Error.Code})");
            return false;
        }

        if(network.ToLower() == "mainnet")
            return status.Response.PeerCount > 0;
        else
            return status.Response.PeerCount >= 0;
    }
}