using System.Data;
using Autofac;
using AutoMapper;
using Miningcore.Blockchain.Cryptonote.Configuration;
using Miningcore.Blockchain.Cryptonote.DaemonRequests;
using Miningcore.Blockchain.Cryptonote.DaemonResponses;
using Miningcore.Configuration;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Native;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Time;
using Miningcore.Util;
using Newtonsoft.Json;
using Contract = Miningcore.Contracts.Contract;
using CNC = Miningcore.Blockchain.Cryptonote.CryptonoteCommands;
using Newtonsoft.Json.Linq;

namespace Miningcore.Blockchain.Cryptonote;

[CoinFamily(CoinFamily.Cryptonote)]
public class CryptonotePayoutHandler : PayoutHandlerBase,
    IPayoutHandler
{
    public CryptonotePayoutHandler(
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

    private readonly IComponentContext ctx;
    private RpcClient rpcClient;
    private RpcClient rpcClientWallet;
    private CryptonoteNetworkType? networkType;
    private CryptonotePoolPaymentProcessingConfigExtra extraConfig;
    private bool walletSupportsTransferSplit;
    private string zephyrWalletAsset;   // asset name that carried the wallet balance in EnsureBalance (ZPH or ZEPH)

    protected override string LogCategory => "Cryptonote Payout Handler";

    /// <summary>
    /// Zephyr: spend the asset the balance was found under. wallet-rpc defaults both assets to ZPH, which fails with
    /// "not enough money" on pre-v11 chains whose outputs are typed ZEPH (seen on the private testnet, 2026-10-31).
    /// </summary>
    private void ApplyZephyrAssets(TransferRequest request, CryptonoteCoinTemplate coin)
    {
        if(coin.Symbol != "ZEPH" || string.IsNullOrEmpty(zephyrWalletAsset))
            return;

        request.SourceAsset = zephyrWalletAsset;
        request.ZephyrDestinationAsset = zephyrWalletAsset;
    }

    private async Task<bool> HandleTransferResponseAsync(RpcResponse<TransferResponse> response, params Balance[] balances)
    {
        var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

        if(response.Error == null)
        {
            var txHash = response.Response.TxHash;
            var txFee = response.Response.Fee / coin.SmallestUnit;

            logger.Info(() => $"[{LogCategory}] Payment transaction id: {txHash}, TxFee {FormatAmount(txFee)}, TxKey {response.Response.TxKey}");

            await PersistPaymentsAsync(balances, txHash);
            NotifyPayoutSuccess(poolConfig.Id, balances, new[] { txHash }, txFee);
            return true;
        }

        else
        {
            logger.Error(() => $"[{LogCategory}] Daemon command '{CryptonoteWalletCommands.Transfer}' returned error: {response.Error.Message} code {response.Error.Code}");

            NotifyPayoutFailure(poolConfig.Id, balances, $"Daemon command '{CryptonoteWalletCommands.Transfer}' returned error: {response.Error.Message} code {response.Error.Code}", null, response.Error.Code);
            return false;
        }
    }

    private async Task<bool> HandleTransferResponseAsync(RpcResponse<TransferSplitResponse> response, params Balance[] balances)
    {
        var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

        if(response.Error == null)
        {
            var txHashes = response.Response.TxHashList;
            var txFees = response.Response.FeeList.Select(x => x / coin.SmallestUnit).ToArray();

            logger.Info(() => $"[{LogCategory}] Split-Payment transaction ids: {string.Join(", ", txHashes)}, Corresponding TxFees were {string.Join(", ", txFees.Select(FormatAmount))}");

            await PersistPaymentsAsync(balances, txHashes.First());
            NotifyPayoutSuccess(poolConfig.Id, balances, txHashes, txFees.Sum());
            return true;
        }

        else
        {
            logger.Error(() => $"[{LogCategory}] Daemon command '{CryptonoteWalletCommands.TransferSplit}' returned error: {response.Error.Message} code {response.Error.Code}");

            // hashlak: transfer_split commits its transactions one by one — one error code for the whole request says
            // nothing about how many already left; no code is passed, so the guard treats every payee as unresolved
            NotifyPayoutFailure(poolConfig.Id, balances, $"Daemon command '{CryptonoteWalletCommands.TransferSplit}' returned error: {response.Error.Message} code {response.Error.Code}", null);
            return false;
        }
    }

    private async Task UpdateNetworkTypeAsync(CancellationToken ct)
    {
        if(!networkType.HasValue)
        {
            var infoResponse = await rpcClient.ExecuteAsync(logger, CNC.GetInfo, ct, true);
            var info = infoResponse.Response.ToObject<GetInfoResponse>();

            if(info == null)
                throw new PoolStartupException($"{LogCategory}] Unable to determine network type", poolConfig.Id);

            // chain detection
            if(!string.IsNullOrEmpty(info.NetType))
            {
                switch(info.NetType.ToLower())
                {
                    case "mainnet":
                        networkType = CryptonoteNetworkType.Main;
                        break;
                    case "stagenet":
                        networkType = CryptonoteNetworkType.Stage;
                        break;
                    case "testnet":
                        networkType = CryptonoteNetworkType.Test;
                        break;
                    default:
                        throw new PoolStartupException($"Unsupported net type '{info.NetType}'", poolConfig.Id);
                }
            }

            else
                networkType = info.IsTestnet ? CryptonoteNetworkType.Test : CryptonoteNetworkType.Main;
        }
    }

    /// <summary>
    /// Zephyr: amount of the miner's own coinbase output (miner_tx.vout[0]) in atomic units, read via get_block.
    /// Returns null when the daemon or the block JSON cannot be read - callers must leave the block pending, never estimate.
    /// </summary>
    private async Task<decimal?> GetZephyrMinerRewardAsync(ulong height, string hash, ulong headerReward, CancellationToken ct)
    {
        // by hash: the coinbase must belong to the exact block whose orphan status was just checked
        var response = await rpcClient.ExecuteAsync<GetBlockResponse>(logger, CNC.GetBlock, ct, new GetBlockRequest { Hash = hash });

        if(response.Error != null)
        {
            logger.Warn(() => $"[{LogCategory}] Daemon command '{CNC.GetBlock}' returned error: {response.Error.Message} code {response.Error.Code} for block {height}");
            return null;
        }

        if(string.IsNullOrEmpty(response.Response?.Json))
        {
            logger.Warn(() => $"[{LogCategory}] Daemon command '{CNC.GetBlock}' returned no block json for block {height}");
            return null;
        }

        try
        {
            // testnet HF 3 and mainnet HF 11 both serialize outputs as
            // {"amount": <integer>, "target": {"tagged_key": {"key": "..", "asset_type": "ZEPH"|"ZPH", "view_tag": ".."}}}
            var vout = JObject.Parse(response.Response.Json)["miner_tx"]?["vout"] as JArray;
            var first = vout?.FirstOrDefault() as JObject;
            var amountToken = first?["amount"];

            if(amountToken == null || amountToken.Type != JTokenType.Integer)
            {
                logger.Warn(() => $"[{LogCategory}] Block {height}: miner_tx.vout[0].amount is missing or not an integer (outputs: {vout?.Count ?? 0})");
                return null;
            }

            var amount = amountToken.Value<ulong>();
            var assetType = (first["target"] as JObject)?.Properties().FirstOrDefault()?.Value?["asset_type"]?.Value<string>();

            if(amount == 0 || amount > headerReward)
            {
                logger.Warn(() => $"[{LogCategory}] Block {height}: vout[0].amount {amount} is zero or exceeds the header reward {headerReward}");
                return null;
            }

            if(string.IsNullOrEmpty(assetType) || !ZephyrConstants.WalletAssetNames.Contains(assetType))
            {
                logger.Warn(() => $"[{LogCategory}] Block {height}: vout[0] asset '{assetType ?? "?"}' is not the base coin; refusing to credit it");
                return null;
            }

            return amount;
        }

        catch(Exception ex)
        {
            logger.Warn(() => $"[{LogCategory}] Block {height}: cannot parse block json: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> EnsureBalance(decimal requiredAmount, CryptonoteCoinTemplate coin, CancellationToken ct)
    {
        var wallet = await ReadWalletBalanceAsync(coin, ct);

        if(wallet == null)
            return false;

        if(wallet.Unlocked < requiredAmount)
        {
            logger.Info(() => $"[{LogCategory}] {FormatAmount(requiredAmount)} unlocked balance required for payment, but only have {FormatAmount(wallet.Unlocked.Value)} of {FormatAmount(wallet.Total)} available yet. Will try again.");
            return false;
        }

        logger.Info(() => $"[{LogCategory}] Current balance is {FormatAmount(wallet.Unlocked.Value)}");
        return true;
    }

    // hashlak payout guard: the same wallet query serves the guard's hot-wallet check (null = RPC error)
    public override Task<WalletBalance> GetWalletBalanceAsync(CancellationToken ct)
    {
        return ReadWalletBalanceAsync(poolConfig.Template.As<CryptonoteCoinTemplate>(), ct);
    }

    // the wallet receives Math.Floor(amount × SmallestUnit): reserve exactly that
    public override decimal NormalizePayoutAmount(decimal amount)
    {
        var unit = poolConfig.Template.As<CryptonoteCoinTemplate>().SmallestUnit;

        return Math.Floor(amount * unit) / unit;
    }

    // wallet-rpc errors raised before signing (wallet_rpc_server_error_codes.h): WRONG_ADDRESS -2, WRONG_PAYMENT_ID -5,
    // TX_NOT_POSSIBLE -16, NOT_ENOUGH_MONEY -17, ZERO_DESTINATION -20. The generic -4 and everything else may have relayed.
    protected override bool IsNotSentErrorCode(int rpcErrorCode)
    {
        return IsNotSentCode(rpcErrorCode);
    }

    /// <summary>
    /// wallet-rpc `transfer` errors raised before a transaction is relayed (wallet_rpc_server_error_codes.h, identical in
    /// Monero and Zephyr v2.3.0 — verified 2026-11-01): WRONG_ADDRESS (-2), WRONG_PAYMENT_ID (-5), TX_NOT_POSSIBLE (-16),
    /// NOT_ENOUGH_MONEY (-17), ZERO_DESTINATION (-20), NOT_ENOUGH_UNLOCKED_MONEY (-37) — all thrown by validation or by
    /// create_transactions_2 before anything reaches the daemon. GENERIC_TRANSFER_ERROR (-4: also "rejected by daemon"
    /// after a relay attempt), DAEMON_IS_BUSY (-3), TX_TOO_LARGE (-18: handled by the transfer_split retry, otherwise a
    /// human looks), NO_DAEMON_CONNECTION (-38: the transaction may still be in the daemon), UNKNOWN_ERROR (-1) and -500
    /// (RpcClient timeout) stay unresolved. transfer_split is never classified: its outcome is per-transaction.
    /// </summary>
    internal static bool IsNotSentCode(int rpcErrorCode)
    {
        return rpcErrorCode is -2 or -5 or -16 or -17 or -20 or -37;
    }

    private static bool IsTooLargeError(string message)
    {
        return !string.IsNullOrEmpty(message) &&
            (message.Contains("too big", StringComparison.OrdinalIgnoreCase) || message.Contains("too large", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<WalletBalance> ReadWalletBalanceAsync(CryptonoteCoinTemplate coin, CancellationToken ct)
    {
        decimal unlockedBalance = 0.0m;
        decimal balance = 0.0m;

        // not all Cryptonote coins are equal
        switch(coin.Symbol)
        {
            case "SAL":
            case "ZEPH":
                // Zephyr: hard fork v11 (mainnet 536000) renamed the base asset ZEPH -> ZPH and wallet-rpc v2.3.0 defaults every
                // asset parameter to ZPH; chains that never reached v11 (testnet/stagenet stop at HF 3) still report ZEPH.
                // Ask for all assets and take the FIRST matching name - never sum two names for the same coins.
                var isZephyr = coin.Symbol == "ZEPH";
                var walletAssetNames = isZephyr ? ZephyrConstants.WalletAssetNames : new[] { coin.Symbol };
                var balanceRequest = isZephyr ? new GetBalanceRequest { AllAssets = true } : null;

                var responseBalances = await rpcClientWallet.ExecuteAsync<GetBalancesResponse>(logger, CryptonoteWalletCommands.GetBalance, ct, balanceRequest);

                if(responseBalances.Error != null)
                {
                    logger.Error(() => $"[{LogCategory}] Daemon command '{CryptonoteWalletCommands.GetBalance}' returned error: {responseBalances.Error.Message} code {responseBalances.Error.Code}");
                    return null;
                }

                // first name with coins wins (a wallet may list ZPH with 0 next to a funded ZEPH on pre-v11 chains);
                // with nothing funded, the first present entry is used. Never summed.
                var entries = responseBalances.Response?.Balances ?? Array.Empty<BalanceAsset>();
                var assetBalance = walletAssetNames
                    .Select(name => entries.FirstOrDefault(x => x?.Asset == name && (x.Balance > 0 || x.UnlockedBalance > 0)))
                    .FirstOrDefault(x => x != null)
                    ?? walletAssetNames
                    .Select(name => entries.FirstOrDefault(x => x?.Asset == name))
                    .FirstOrDefault(x => x != null);

                if(assetBalance != null)
                {
                    unlockedBalance = assetBalance.UnlockedBalance / coin.SmallestUnit;
                    balance = assetBalance.Balance / coin.SmallestUnit;

                    if(isZephyr)
                        zephyrWalletAsset = assetBalance.Asset;
                }
                else
                    logger.Warn(() => $"[{LogCategory}] Wallet returned no balance entry for {string.Join("/", walletAssetNames)} (assets seen: {string.Join(",", entries.Select(x => x?.Asset ?? "?"))})");
                break;
            default:
                var responseBalance = await rpcClientWallet.ExecuteAsync<GetBalanceResponse>(logger, CryptonoteWalletCommands.GetBalance, ct);

                if(responseBalance.Error != null)
                {
                    logger.Error(() => $"[{LogCategory}] Daemon command '{CryptonoteWalletCommands.GetBalance}' returned error: {responseBalance.Error.Message} code {responseBalance.Error.Code}");
                    return null;
                }

                unlockedBalance = responseBalance.Response.UnlockedBalance / coin.SmallestUnit;
                balance = responseBalance.Response.Balance / coin.SmallestUnit;
                break;
        }

        return new WalletBalance(balance, unlockedBalance);
    }

    private async Task<bool> PayoutBatch(Balance[] balances, CancellationToken ct)
    {
        var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

        // ensure there's enough balance
        if(!await EnsureBalance(balances.Sum(x => x.Amount), coin, ct))
        {
            NotifyPayoutNotSent(balances, "wallet balance unavailable or unlocked balance short");
            return false;
        }

        TransferRequest request;

        // not all Cryptonote coins are equal
        switch(coin.Symbol)
        {
            case "SAL":
                // build request
                request = new TransferRequest
                {
                    Destinations = balances
                        .Where(x => x.Amount > 0)
                        .Select(x =>
                        {
                            ExtractAddressAndPaymentId(x.Address, out var address, out _);

                            return new TransferDestination
                            {
                                Address = address,
                                Amount = (ulong) Math.Floor(x.Amount * coin.SmallestUnit),
                                AssetType = coin.Symbol
                            };
                        }).ToArray(),

                    TransactionType = (uint) SalviumTransactionType.Transfer,
                    SourceAsset = coin.Symbol,
                    DestinationAsset = coin.Symbol,
                    GetTxKey = true
                };
                break;
            default:
                // build request
                request = new TransferRequest
                {
                    Destinations = balances
                        .Where(x => x.Amount > 0)
                        .Select(x =>
                        {
                            ExtractAddressAndPaymentId(x.Address, out var address, out _);

                            return new TransferDestination
                            {
                                Address = address,
                                Amount = (ulong) Math.Floor(x.Amount * coin.SmallestUnit)
                            };
                        }).ToArray(),

                    GetTxKey = true
                };
                break;
        }

        if(request.Destinations.Length == 0)
            return true;

        ApplyZephyrAssets(request, coin);

        logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balances.Sum(x => x.Amount))} to {balances.Length} addresses:\n{string.Join("\n", balances.OrderByDescending(x => x.Amount).Select(x => $"{FormatAmount(x.Amount)} to {x.Address}"))}");

        // send command
        var transferResponse = await rpcClientWallet.ExecuteAsync<TransferResponse>(logger, CryptonoteWalletCommands.Transfer, ct, request);

        // gracefully handle "transaction would be too large" (try /transfer_split)
        // hashlak: -4 is the wallet's GENERIC transfer error; only retry when the message really says the tx is too big,
        // otherwise a second send would follow an error whose transaction may already have been relayed. Current wallets
        // (Monero ≥ 0.13, Zephyr) report it as TX_TOO_LARGE (-18), raised by create_transactions_2 before any relay.
        if(transferResponse.Error?.Code is -4 or -18 && IsTooLargeError(transferResponse.Error.Message))
        {
            if(walletSupportsTransferSplit)
            {
                logger.Error(() => $"[{LogCategory}] Daemon command '{CryptonoteWalletCommands.Transfer}' returned error: {transferResponse.Error.Message} code {transferResponse.Error.Code}");
                logger.Info(() => $"[{LogCategory}] Retrying transfer using {CryptonoteWalletCommands.TransferSplit}");

                var transferSplitResponse = await rpcClientWallet.ExecuteAsync<TransferSplitResponse>(logger, CryptonoteWalletCommands.TransferSplit, ct, request);

                return await HandleTransferResponseAsync(transferSplitResponse, balances);
            }
        }

        return await HandleTransferResponseAsync(transferResponse, balances);
    }

    private void ExtractAddressAndPaymentId(string input, out string address, out string paymentId)
    {
        paymentId = null;
        var index = input.IndexOf(PayoutConstants.PayoutInfoSeperator);

        if(index != -1)
        {
            address = input[..index];

            if(index + 1 < input.Length)
            {
                paymentId = input[(index + 1)..];

                // ignore invalid payment ids
                if(paymentId.Length != CryptonoteConstants.PaymentIdHexLength)
                    paymentId = null;
            }
        }

        else
            address = input;
    }

    private async Task<bool> PayoutToPaymentId(Balance balance, CancellationToken ct)
    {
        var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

        ExtractAddressAndPaymentId(balance.Address, out var address, out var paymentId);
        var isIntegratedAddress = string.IsNullOrEmpty(paymentId);

        // ensure there's enough balance
        if(!await EnsureBalance(balance.Amount, coin, ct))
        {
            NotifyPayoutNotSent(new[] { balance }, "wallet balance unavailable or unlocked balance short");
            return false;
        }

        TransferRequest request;

        // not all Cryptonote coins are equal
        switch(coin.Symbol)
        {
            case "SAL":
                // build request
                request = new TransferRequest
                {
                    Destinations = new[]
                    {
                        new TransferDestination
                        {
                            Address = address,
                            Amount = (ulong) Math.Floor(balance.Amount * coin.SmallestUnit),
                            AssetType = coin.Symbol
                        }
                    },

                    TransactionType = (uint) SalviumTransactionType.Transfer,
                    PaymentId = paymentId,
                    SourceAsset = coin.Symbol,
                    DestinationAsset = coin.Symbol,
                    GetTxKey = true
                };
                break;
            default:
                // build request
                request = new TransferRequest
                {
                    Destinations = new[]
                    {
                        new TransferDestination
                        {
                            Address = address,
                            Amount = (ulong) Math.Floor(balance.Amount * coin.SmallestUnit)
                        }
                    },

                    PaymentId = paymentId,
                    GetTxKey = true
                };
                break;
        }

        if(!isIntegratedAddress)
            request.PaymentId = paymentId;

        ApplyZephyrAssets(request, coin);

        if(!isIntegratedAddress)
            logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balance.Amount)} to address {balance.Address} with paymentId {paymentId}");
        else
            logger.Info(() => $"[{LogCategory}] Paying {FormatAmount(balance.Amount)} to integrated address {balance.Address}");

        // send command
        var result = await rpcClientWallet.ExecuteAsync<TransferResponse>(logger, CryptonoteWalletCommands.Transfer, ct, request);

        if(walletSupportsTransferSplit)
        {
            // gracefully handle error -4 (transaction would be too large. try /transfer_split) — hashlak: only when the
            // message says so (see PayoutBatch)
            if(result.Error?.Code == -4 && IsTooLargeError(result.Error.Message))
            {
                logger.Info(() => $"[{LogCategory}] Retrying transfer using {CryptonoteWalletCommands.TransferSplit}");

                // hashlak: transfer_split answers with tx_hash_list, not tx_hash — reading it as TransferResponse left the
                // transaction id null, so the payment was sent but never persisted (and would have been paid again)
                var splitResult = await rpcClientWallet.ExecuteAsync<TransferSplitResponse>(logger, CryptonoteWalletCommands.TransferSplit, ct, request);

                return await HandleTransferResponseAsync(splitResult, balance);
            }
        }

        return await HandleTransferResponseAsync(result, balance);
    }

    #region IPayoutHandler

    public async Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct)
    {
        Contract.RequiresNonNull(pc);

        poolConfig = pc;
        clusterConfig = cc;
        extraConfig = pc.PaymentProcessing.Extra.SafeExtensionDataAs<CryptonotePoolPaymentProcessingConfigExtra>();

        logger = LogUtil.GetPoolScopedLogger(typeof(CryptonotePayoutHandler), pc);

        // configure standard daemon
        var jsonSerializerSettings = ctx.Resolve<JsonSerializerSettings>();

        var daemonEndpoints = pc.Daemons
            .Where(x => string.IsNullOrEmpty(x.Category))
            .Select(x =>
            {
                if(string.IsNullOrEmpty(x.HttpPath))
                    x.HttpPath = CryptonoteConstants.DaemonRpcLocation;

                return x;
            })
            .ToArray();

        rpcClient = new RpcClient(daemonEndpoints.First(), jsonSerializerSettings, messageBus, pc.Id);

        // configure wallet daemon
        var walletDaemonEndpoints = pc.Daemons
            .Where(x => x.Category?.ToLower() == CryptonoteConstants.WalletDaemonCategory)
            .Select(x =>
            {
                if(string.IsNullOrEmpty(x.HttpPath))
                    x.HttpPath = CryptonoteConstants.DaemonRpcLocation;

                return x;
            })
            .ToArray();

        rpcClientWallet = new RpcClient(walletDaemonEndpoints.First(), jsonSerializerSettings, messageBus, pc.Id);

        // detect network
        await UpdateNetworkTypeAsync(ct);

        // detect transfer_split support
        var response = await rpcClientWallet.ExecuteAsync<TransferResponse>(logger, CryptonoteWalletCommands.TransferSplit, ct);
        walletSupportsTransferSplit = response.Error.Code != CryptonoteConstants.MoneroRpcMethodNotFound;
    }

    public async Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct)
    {
        Contract.RequiresNonNull(poolConfig);
        Contract.RequiresNonNull(blocks);

        var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();
        var pageSize = 100;
        var pageCount = (int) Math.Ceiling(blocks.Length / (double) pageSize);
        var result = new List<Block>();

        // Lowest height first, and once a block cannot be evaluated no higher block is confirmed in this run:
        // the PPLNS scheme deletes shares up to each confirmed block's cutoff, which would eat the share window
        // of the older block that is still pending (hashlak 2026-10-31).
        blocks = blocks.OrderBy(x => x.BlockHeight).ToArray();
        var holdHigherBlocks = false;

        for(var i = 0; i < pageCount; i++)
        {
            // get a page full of blocks
            var page = blocks
                .Skip(i * pageSize)
                .Take(pageSize)
                .ToArray();

            // NOTE: monerod does not support batch-requests
            for(var j = 0; j < page.Length; j++)
            {
                var block = page[j];

                if(holdHigherBlocks)
                    continue;

                var rpcResult = await rpcClient.ExecuteAsync<GetBlockHeaderResponse>(logger,
                    CNC.GetBlockHeaderByHeight, ct,
                    new GetBlockHeaderByHeightRequest
                    {
                        Height = block.BlockHeight
                    });

                if(rpcResult.Error != null)
                {
                    logger.Debug(() => $"[{LogCategory}] Daemon reports error '{rpcResult.Error.Message}' (Code {rpcResult.Error.Code}) for block {block.BlockHeight}");
                    holdHigherBlocks = true;
                    continue;
                }

                if(rpcResult.Response?.BlockHeader == null)
                {
                    logger.Debug(() => $"[{LogCategory}] Daemon returned no header for block {block.BlockHeight}");
                    holdHigherBlocks = true;
                    continue;
                }

                var blockHeader = rpcResult.Response.BlockHeader;

                // update progress
                block.ConfirmationProgress = Math.Min(1.0d, (double) blockHeader.Depth / CryptonoteConstants.PayoutMinBlockConfirmations);
                result.Add(block);

                messageBus.NotifyBlockConfirmationProgress(poolConfig.Id, block, coin);

                // orphaned?
                if(blockHeader.IsOrphaned || blockHeader.Hash != block.TransactionConfirmationData)
                {
                    block.Status = BlockStatus.Orphaned;
                    block.Reward = 0;

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                    continue;
                }

                // matured and spendable?
                if(blockHeader.Depth >= CryptonoteConstants.PayoutMinBlockConfirmations)
                {
                    decimal reward;

                    // not all Cryptonote coins are equal
                    switch(coin.Symbol)
                    {
                        case "ZEPH":
                            // Zephyr: the header "reward" is the sum of every miner_tx output. The reserve (20 %, 30 % from HF 6)
                            // and yield (5 % from HF 6) rewards are NOT outputs, the pre-HF-6 governance output (5 %) IS one, and
                            // fees paid in other assets (ZSD/ZRS) are added as separate outputs. What the pool wallet actually
                            // receives is exactly miner_tx.vout[0]; the old ratio formula credited 95 % of it on mainnet.
                            var minerReward = await GetZephyrMinerRewardAsync(block.BlockHeight, blockHeader.Hash, blockHeader.Reward, ct);

                            if(!minerReward.HasValue)
                            {
                                if(blockHeader.Depth > 2 * CryptonoteConstants.PayoutMinBlockConfirmations)
                                    logger.Error(() => $"[{LogCategory}] Block {block.BlockHeight} has been mature for {blockHeader.Depth} confirmations and still cannot be credited - payouts for this pool are stalled, investigate the daemon");
                                else
                                    logger.Warn(() => $"[{LogCategory}] Block {block.BlockHeight} is mature but its coinbase could not be read; leaving it pending until the next run");

                                holdHigherBlocks = true;
                                continue;
                            }

                            reward = (minerReward.Value / coin.SmallestUnit) * coin.BlockrewardMultiplier;
                            break;
                        default:
                            reward = (blockHeader.Reward / coin.SmallestUnit) * coin.BlockrewardMultiplier;
                            break;
                    }

                    block.Status = BlockStatus.Confirmed;
                    block.ConfirmationProgress = 1;
                    block.Reward = reward;

                    logger.Info(() => $"[{LogCategory}] Unlocked block {block.BlockHeight} worth {FormatAmount(block.Reward)}");

                    messageBus.NotifyBlockUnlocked(poolConfig.Id, block, coin);
                }
            }
        }

        return result.ToArray();
    }

    public override async Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx,
        IMiningPool pool, Block block, CancellationToken ct)
    {
        var blockRewardRemaining = await base.UpdateBlockRewardBalancesAsync(con, tx, pool, block, ct);

        // Deduct static reserve for tx fees
        blockRewardRemaining -= CryptonoteConstants.StaticTransactionFeeReserve;

        return blockRewardRemaining;
    }

    public async Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct)
    {
        Contract.RequiresNonNull(balances);

        var coin = poolConfig.Template.As<CryptonoteCoinTemplate>();

#if !DEBUG // ensure we have peers
            var infoResponse = await rpcClient.ExecuteAsync<GetInfoResponse>(logger, CNC.GetInfo, ct);
            if (infoResponse.Error != null || infoResponse.Response == null ||
                infoResponse.Response.IncomingConnectionsCount + infoResponse.Response.OutgoingConnectionsCount < 2)
            {
                logger.Warn(() => $"[{LogCategory}] Payout aborted. Not enough peers (4 required)");
                NotifyPayoutNotSent(balances, "not enough peers");
                return;
            }
#endif
        // validate addresses
        var requested = balances;

        balances = balances
            .Where(x =>
            {
                ExtractAddressAndPaymentId(x.Address, out var address, out _);

                var addressPrefix = CryptonoteBindings.DecodeAddress(address);
                var addressIntegratedPrefix = CryptonoteBindings.DecodeIntegratedAddress(address);

                switch(networkType)
                {
                    case CryptonoteNetworkType.Main:
                        if(addressPrefix != coin.AddressPrefix &&
                           addressPrefix != coin.SubAddressPrefix &&
                           addressIntegratedPrefix != coin.AddressPrefixIntegrated)
                        {
                            logger.Warn(() => $"[{LogCategory}] Excluding payment to invalid address: {x.Address}");
                            return false;
                        }

                        break;

                    case CryptonoteNetworkType.Stage:
                        if(addressPrefix != coin.AddressPrefixStagenet &&
                           addressPrefix != coin.SubAddressPrefixStagenet &&
                           addressIntegratedPrefix != coin.AddressPrefixIntegratedStagenet)
                        {
                            logger.Warn(() => $"[{LogCategory}] Excluding payment to invalid address: {x.Address}");
                            return false;
                        }

                        break;

                    case CryptonoteNetworkType.Test:
                        if(addressPrefix != coin.AddressPrefixTestnet &&
                           addressPrefix != coin.SubAddressPrefixTestnet &&
                           addressIntegratedPrefix != coin.AddressPrefixIntegratedTestnet)
                        {
                            logger.Warn(() => $"[{LogCategory}] Excluding payment to invalid address: {x.Address}");
                            return false;
                        }

                        break;
                }

                return true;
            })
            .ToArray();

        // hashlak payout guard: excluded addresses are certainly not paid this run
        NotifyPayoutNotSent(requested.Except(balances).ToArray(), "invalid address for this network");

        // simple balances first
        var simpleBalances = balances
            .Where(x =>
            {
                ExtractAddressAndPaymentId(x.Address, out var address, out var paymentId);

                var hasPaymentId = paymentId != null;
                var isIntegratedAddress = false;
                var addressIntegratedPrefix = CryptonoteBindings.DecodeIntegratedAddress(address);

                switch(networkType)
                {
                    case CryptonoteNetworkType.Main:
                        if(addressIntegratedPrefix == coin.AddressPrefixIntegrated)
                            isIntegratedAddress = true;
                        break;

                    case CryptonoteNetworkType.Test:
                        if(addressIntegratedPrefix == coin.AddressPrefixIntegratedTestnet)
                            isIntegratedAddress = true;
                        break;
                }

                return !hasPaymentId && !isIntegratedAddress;
            })
            .OrderByDescending(x => x.Amount)
            .ToArray();

        if(simpleBalances.Length > 0)
#if false
                await PayoutBatch(simpleBalances);
#else
        {
            var maxBatchSize = extraConfig?.MaximumDestinationPerTransfer ?? 15;  // going over 15 yields "sv/gamma are too large"
            var pageSize = maxBatchSize;
            var pageCount = (int) Math.Ceiling((double) simpleBalances.Length / pageSize);

            logger.Info(() => $"[{LogCategory}] Maximum of simultaneous destination address in a single transaction: {maxBatchSize}");

            for(var i = 0; i < pageCount; i++)
            {
                var page = simpleBalances
                    .Skip(i * pageSize)
                    .Take(pageSize)
                    .ToArray();

                if(!await PayoutBatch(page, ct))
                {
                    // hashlak payout guard: the pages that were never attempted are certainly not paid
                    NotifyPayoutNotSent(simpleBalances.Skip((i + 1) * pageSize).ToArray(), "previous batch failed");
                    break;
                }
            }
        }
#endif
        // balances with paymentIds
        var minimumPaymentToPaymentId = extraConfig?.MinimumPaymentToPaymentId ?? poolConfig.PaymentProcessing.MinimumPayment;

        var paymentIdCandidates = balances.Except(simpleBalances).ToArray();

        var paymentIdBalances = paymentIdCandidates
            .Where(x => x.Amount >= minimumPaymentToPaymentId)
            .ToArray();

        NotifyPayoutNotSent(paymentIdCandidates.Except(paymentIdBalances).ToArray(), "below minimumPaymentToPaymentId");

        for(var i = 0; i < paymentIdBalances.Length; i++)
        {
            if(!await PayoutToPaymentId(paymentIdBalances[i], ct))
            {
                NotifyPayoutNotSent(paymentIdBalances.Skip(i + 1).ToArray(), "previous transfer failed");
                break;
            }
        }

        // save wallet
        await rpcClientWallet.ExecuteAsync<JToken>(logger, CryptonoteWalletCommands.Store, ct);
    }

    public double AdjustBlockEffort(double effort)
    {
        return effort;
    }

    #endregion // IPayoutHandler
}
