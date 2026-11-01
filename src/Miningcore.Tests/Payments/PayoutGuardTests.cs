using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Notifications.Messages;
using Miningcore.Payments;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PayoutGuardTests
{
    private readonly IConnectionFactory cf = Substitute.For<IConnectionFactory>();
    private readonly IDbConnection con = Substitute.For<IDbConnection>();
    private readonly IDbTransaction tx = Substitute.For<IDbTransaction>();
    private readonly IPayoutGuardRepository repo = Substitute.For<IPayoutGuardRepository>();
    private readonly IBalanceRepository balanceRepo = Substitute.For<IBalanceRepository>();
    private readonly IMessageBus bus = Substitute.For<IMessageBus>();
    private readonly FakeHandler handler = new();
    private readonly PayoutGuard guard;

    private static readonly Balance[] candidates = { B("a", 5), B("b", 25), B("fee", 2) };

    public PayoutGuardTests()
    {
        cf.OpenConnectionAsync().Returns(Task.FromResult(con));
        con.BeginTransaction(Arg.Any<IsolationLevel>()).Returns(tx);

        repo.GetStateAsync(con, "p").Returns(Task.FromResult<PayoutGuardState>(null));
        repo.GetOpenRunsAsync(con, "p").Returns(Task.FromResult(Array.Empty<PayoutRun>()));
        repo.GetStateForUpdateAsync(con, tx, "p").Returns(Task.FromResult(new PayoutGuardState { PoolId = "p" }));
        repo.GetPaidLast24hAsync(con, tx, "p").Returns(Task.FromResult(0m));
        repo.GetCreditedLast24hAsync(con, tx, "p").Returns(Task.FromResult(0m));
        repo.GetCreditedBlocksLast24hAsync(con, tx, "p").Returns(Task.FromResult(0));
        repo.GetBalanceForUpdateAsync(con, tx, "p", Arg.Any<string>()).Returns(x => Task.FromResult<decimal?>(candidates.First(b => b.Address == x.ArgAt<string>(3)).Amount));

        guard = new PayoutGuard(cf, repo, balanceRepo, bus);
    }

    private static Balance B(string address, decimal amount) => new() { PoolId = "p", Address = address, Amount = amount };

    private static PoolConfig Pool(PayoutGuardConfig guardConfig) => new()
    {
        Id = "p",
        Address = "poolwallet",
        Template = new BitcoinTemplate { Symbol = "TST" },
        RewardRecipients = new[] { new RewardRecipient { Address = "fee", Percentage = 0 } },
        PaymentProcessing = new PoolPaymentProcessingConfig { Enabled = true, MinimumPayment = 1, Guard = guardConfig }
    };

    private static PayoutGuardConfig Config() => new() { PerPayoutCap = 10, DailyCap = 100, NominalBlockReward = 50 };

    [Fact]
    public async Task Missing_Config_Trips_And_Skips()
    {
        var decision = await guard.PrepareRunAsync(Pool(null), handler, candidates, CancellationToken.None);

        Assert.IsType<PayoutGuardDecision.Skip>(decision);
        await repo.Received(1).TripAsync(con, tx, "p", Arg.Is<string>(s => s.Contains("guard config missing")));
        bus.Received(1).SendMessage(Arg.Any<AdminNotification>());
        await repo.DidNotReceiveWithAnyArgs().InsertRunAsync(default, default, default, default);
    }

    [Fact]
    public async Task Disabled_Guard_Is_Legacy()
    {
        var decision = await guard.PrepareRunAsync(Pool(new PayoutGuardConfig { Enabled = false }), handler, candidates, CancellationToken.None);

        Assert.IsType<PayoutGuardDecision.Legacy>(decision);
        await repo.DidNotReceiveWithAnyArgs().GetStateAsync(default, default);
    }

    [Fact]
    public async Task Tripped_Pool_Skips_Without_Writes()
    {
        repo.GetStateAsync(con, "p").Returns(Task.FromResult(new PayoutGuardState { PoolId = "p", Tripped = true, TrippedReason = "manual" }));

        var decision = await guard.PrepareRunAsync(Pool(Config()), handler, candidates, CancellationToken.None);

        var skip = Assert.IsType<PayoutGuardDecision.Skip>(decision);
        Assert.Contains("manual", skip.Reason);
        await repo.DidNotReceiveWithAnyArgs().TripAsync(default, default, default, default);
        await repo.DidNotReceiveWithAnyArgs().InsertRunAsync(default, default, default, default);
    }

    [Fact]
    public async Task Open_Run_Trips()
    {
        repo.GetOpenRunsAsync(con, "p").Returns(Task.FromResult(new[] { new PayoutRun { RunId = Guid.NewGuid(), PoolId = "p", Status = PayoutRunStatus.Unresolved } }));

        var decision = await guard.PrepareRunAsync(Pool(Config()), handler, candidates, CancellationToken.None);

        Assert.IsType<PayoutGuardDecision.Skip>(decision);
        await repo.Received(1).TripAsync(con, tx, "p", Arg.Is<string>(s => s.Contains("open run")));
    }

    [Fact]
    public async Task Payouts_Disabled_Reserves_Nothing()
    {
        var cfg = Config();
        cfg.PayoutsEnabled = false;

        var decision = await guard.PrepareRunAsync(Pool(cfg), handler, candidates, CancellationToken.None);

        Assert.IsType<PayoutGuardDecision.Skip>(decision);
        await repo.DidNotReceiveWithAnyArgs().InsertRunAsync(default, default, default, default);
        await balanceRepo.DidNotReceiveWithAnyArgs().AddAmountAsync(default, default, default, default, default, default);
        Assert.Equal(0, handler.WalletCalls);
    }

    [Fact]
    public async Task Wallet_Unavailable_Skips_Short_Total_Trips_Short_Unlocked_Skips()
    {
        handler.Wallet = null;
        Assert.IsType<PayoutGuardDecision.Skip>(await guard.PrepareRunAsync(Pool(Config()), handler, candidates, CancellationToken.None));
        await repo.DidNotReceiveWithAnyArgs().TripAsync(default, default, default, default);

        handler.Wallet = new WalletBalance(1, 1);   // batch is 5 + 10 (capped) + 2 = 17
        Assert.IsType<PayoutGuardDecision.Skip>(await guard.PrepareRunAsync(Pool(Config()), handler, candidates, CancellationToken.None));
        await repo.Received(1).TripAsync(con, tx, "p", Arg.Is<string>(s => s.Contains("hot wallet")));

        repo.ClearReceivedCalls();
        handler.Wallet = new WalletBalance(1000, 1);
        Assert.IsType<PayoutGuardDecision.Skip>(await guard.PrepareRunAsync(Pool(Config()), handler, candidates, CancellationToken.None));
        await repo.DidNotReceiveWithAnyArgs().TripAsync(default, default, default, default);
        await repo.DidNotReceiveWithAnyArgs().InsertRunAsync(default, default, default, default);
    }

    [Fact]
    public async Task Happy_Path_Reserves_Capped_Amounts_In_One_Transaction()
    {
        PayoutRun run = null;
        PayoutRunItem[] items = null;
        repo.InsertRunAsync(con, tx, Arg.Do<PayoutRun>(r => run = r), Arg.Do<PayoutRunItem[]>(i => items = i)).Returns(Task.CompletedTask);

        var decision = await guard.PrepareRunAsync(Pool(Config()), handler, candidates, CancellationToken.None);

        var reserved = Assert.IsType<PayoutGuardDecision.Run>(decision);
        Assert.Equal(17m, reserved.Context.Total);
        Assert.Equal(new[] { 5m, 10m, 2m }, reserved.Context.Balances.Select(x => x.Amount));

        Assert.Equal(PayoutRunStatus.Reserved, run.Status);
        Assert.Equal("TST", run.Coin);
        Assert.Equal(17m, run.AmountTotal);
        Assert.Equal(3, run.PayeeCount);
        Assert.True(items.Single(x => x.Address == "fee").Recipient);
        Assert.False(items.Single(x => x.Address == "a").Recipient);
        Assert.All(items, x => Assert.Equal(PayoutRunStatus.Reserved, x.Status));

        await balanceRepo.Received(1).AddAmountAsync(con, tx, "p", "b", -10m, "Payout reserved", $"payout:{run.RunId}");
        await balanceRepo.Received(3).AddAmountAsync(con, tx, "p", Arg.Any<string>(), Arg.Is<decimal>(d => d < 0), "Payout reserved", Arg.Any<string[]>());
        await repo.Received(1).UpdateRunStatsAsync(con, tx, "p", 17m, 0m, 1000m);
        tx.Received(1).Commit();
        tx.DidNotReceive().Rollback();
        await repo.DidNotReceiveWithAnyArgs().TripAsync(default, default, default, default);
    }

    [Fact]
    public async Task Pool_Wallet_Address_Is_Never_Reserved()
    {
        PayoutRunItem[] items = null;
        repo.InsertRunAsync(con, tx, Arg.Any<PayoutRun>(), Arg.Do<PayoutRunItem[]>(i => items = i)).Returns(Task.CompletedTask);
        var withOwn = candidates.Append(B("poolwallet", 9)).ToArray();

        var decision = await guard.PrepareRunAsync(Pool(Config()), handler, withOwn, CancellationToken.None);

        Assert.IsType<PayoutGuardDecision.Run>(decision);
        Assert.DoesNotContain(items, x => x.Address == "poolwallet");
        Assert.Equal(3, items.Length);
        await balanceRepo.DidNotReceive().AddAmountAsync(con, tx, "p", "poolwallet", Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string[]>());

        var onlyOwn = await guard.PrepareRunAsync(Pool(Config()), handler, new[] { B("poolwallet", 9) }, CancellationToken.None);
        Assert.IsType<PayoutGuardDecision.Skip>(onlyOwn);
    }

    [Fact]
    public async Task Daily_Cap_Trips_Inside_The_Transaction_And_Reserves_Nothing()
    {
        repo.GetPaidLast24hAsync(con, tx, "p").Returns(Task.FromResult(90m));

        var decision = await guard.PrepareRunAsync(Pool(Config()), handler, candidates, CancellationToken.None);

        Assert.IsType<PayoutGuardDecision.Skip>(decision);
        await repo.Received(1).TripAsync(con, tx, "p", Arg.Is<string>(s => s.Contains("daily cap")));
        await repo.DidNotReceiveWithAnyArgs().InsertRunAsync(default, default, default, default);
        await balanceRepo.DidNotReceiveWithAnyArgs().AddAmountAsync(default, default, default, default, default, default);
        tx.Received(1).Commit();
        bus.Received(1).SendMessage(Arg.Any<AdminNotification>());
    }

    [Fact]
    public async Task Credit_Anomaly_Trips()
    {
        repo.GetCreditedLast24hAsync(con, tx, "p").Returns(Task.FromResult(200m));
        repo.GetCreditedBlocksLast24hAsync(con, tx, "p").Returns(Task.FromResult(1));   // 1 x 50 x 1.2 = 60 allowed

        var decision = await guard.PrepareRunAsync(Pool(Config()), handler, candidates, CancellationToken.None);

        Assert.IsType<PayoutGuardDecision.Skip>(decision);
        await repo.Received(1).TripAsync(con, tx, "p", Arg.Is<string>(s => s.Contains("credit anomaly")));
        await repo.DidNotReceiveWithAnyArgs().InsertRunAsync(default, default, default, default);
    }

    [Fact]
    public async Task Changed_Balance_Rolls_Back_And_Skips()
    {
        repo.GetBalanceForUpdateAsync(con, tx, "p", "b").Returns(Task.FromResult<decimal?>(1m));

        var decision = await guard.PrepareRunAsync(Pool(Config()), handler, candidates, CancellationToken.None);

        Assert.IsType<PayoutGuardDecision.Skip>(decision);
        tx.Received(1).Rollback();
        tx.DidNotReceive().Commit();
        await repo.DidNotReceiveWithAnyArgs().InsertRunAsync(default, default, default, default);
        await repo.DidNotReceiveWithAnyArgs().TripAsync(default, default, default, default);
    }

    [Fact]
    public async Task Finalize_All_Confirmed_Is_Paid()
    {
        var ctx = new PayoutRunContext(Guid.NewGuid(), "p", new[] { B("a", 5) }, repo);
        repo.GetItemsAsync(con, tx, ctx.RunId).Returns(Task.FromResult(new[] { Item(ctx.RunId, "a", 5, PayoutRunStatus.Paid) }));

        await guard.FinalizeRunAsync(ctx, null, CancellationToken.None);

        await repo.Received(1).UpdateRunStatusAsync(con, tx, ctx.RunId, PayoutRunStatus.Paid, null);
        await repo.DidNotReceiveWithAnyArgs().TripAsync(default, default, default, default);
        await balanceRepo.DidNotReceiveWithAnyArgs().AddAmountAsync(default, default, default, default, default, default);
    }

    [Fact]
    public async Task Finalize_Rejected_Items_Are_Recredited_Without_A_Trip()
    {
        var ctx = new PayoutRunContext(Guid.NewGuid(), "p", new[] { B("a", 5), B("b", 7) }, repo);
        ctx.Reject(new[] { B("b", 7) }, "address rejected by the daemon");
        repo.GetItemsAsync(con, tx, ctx.RunId).Returns(Task.FromResult(new[]
        {
            Item(ctx.RunId, "a", 5, PayoutRunStatus.Paid),
            Item(ctx.RunId, "b", 7, PayoutRunStatus.Reserved)
        }));

        await guard.FinalizeRunAsync(ctx, null, CancellationToken.None);

        await repo.Received(1).MarkItemAsync(con, tx, ctx.RunId, "b", PayoutRunStatus.Failed, "address rejected by the daemon");
        await balanceRepo.Received(1).AddAmountAsync(con, tx, "p", "b", 7m, Arg.Is<string>(s => s.StartsWith("Payout failed")), $"payout:{ctx.RunId}", "payout-recredit");
        await repo.Received(1).UpdateRunStatusAsync(con, tx, ctx.RunId, PayoutRunStatus.Paid, null);
        await repo.DidNotReceiveWithAnyArgs().TripAsync(default, default, default, default);
    }

    [Fact]
    public async Task Finalize_Unconfirmed_Item_Is_Unresolved_And_Trips()
    {
        var ctx = new PayoutRunContext(Guid.NewGuid(), "p", new[] { B("a", 5) }, repo);
        repo.GetItemsAsync(con, tx, ctx.RunId).Returns(Task.FromResult(new[] { Item(ctx.RunId, "a", 5, PayoutRunStatus.Reserved) }));

        await guard.FinalizeRunAsync(ctx, new TimeoutException("sendmany timed out"), CancellationToken.None);

        await repo.Received(1).MarkItemAsync(con, tx, ctx.RunId, "a", PayoutRunStatus.Unresolved, "sendmany timed out");
        await repo.Received(1).UpdateRunStatusAsync(con, tx, ctx.RunId, PayoutRunStatus.Unresolved, Arg.Is<string>(s => s.Contains("unresolved")));
        await repo.Received(1).TripAsync(con, tx, "p", Arg.Is<string>(s => s.Contains(ctx.RunId.ToString())));
        await balanceRepo.DidNotReceiveWithAnyArgs().AddAmountAsync(default, default, default, default, default, default);
        bus.Received(1).SendMessage(Arg.Any<AdminNotification>());
    }

    [Fact]
    public async Task Finalize_Runs_Once()
    {
        var ctx = new PayoutRunContext(Guid.NewGuid(), "p", new[] { B("a", 5) }, repo);
        repo.GetItemsAsync(con, tx, ctx.RunId).Returns(Task.FromResult(new[] { Item(ctx.RunId, "a", 5, PayoutRunStatus.Paid) }));

        await guard.FinalizeRunAsync(ctx, null, CancellationToken.None);
        await guard.FinalizeRunAsync(ctx, null, CancellationToken.None);

        await repo.Received(1).GetItemsAsync(con, tx, ctx.RunId);
    }

    [Fact]
    public async Task Startup_Recovery_Trips_Pools_With_Open_Runs()
    {
        repo.GetOpenRunsAsync(con, "p").Returns(Task.FromResult(new[] { new PayoutRun { RunId = Guid.NewGuid(), PoolId = "p", Status = PayoutRunStatus.Reserved } }));

        await guard.RecoverAtStartupAsync(new[] { Pool(Config()) }, CancellationToken.None);

        await repo.Received(1).TripAsync(con, tx, "p", Arg.Is<string>(s => s.Contains("startup")));
    }

    private static PayoutRunItem Item(Guid runId, string address, decimal amount, string status) => new()
    {
        RunId = runId,
        Address = address,
        Amount = amount,
        Status = status
    };

    /// <summary>A handler stub with explicit members (default interface methods are not intercepted reliably).</summary>
    private sealed class FakeHandler : IPayoutHandler
    {
        public WalletBalance Wallet { get; set; } = new(1000, 1000);
        public int WalletCalls { get; private set; }

        public Task ConfigureAsync(ClusterConfig cc, PoolConfig pc, CancellationToken ct) => Task.CompletedTask;
        public Task<Block[]> ClassifyBlocksAsync(IMiningPool pool, Block[] blocks, CancellationToken ct) => Task.FromResult(blocks);
        public Task<decimal> UpdateBlockRewardBalancesAsync(IDbConnection con, IDbTransaction tx, IMiningPool pool, Block block, CancellationToken ct) => Task.FromResult(0m);
        public Task PayoutAsync(IMiningPool pool, Balance[] balances, CancellationToken ct) => Task.CompletedTask;
        public double AdjustShareDifficulty(double difficulty) => difficulty;
        public double AdjustBlockEffort(double effort) => effort;
        public string FormatAmount(decimal amount) => amount.ToString();

        public void BeginPayoutRun(PayoutRunContext run)
        {
        }

        public Task<WalletBalance> GetWalletBalanceAsync(CancellationToken ct)
        {
            WalletCalls++;
            return Task.FromResult(Wallet);
        }

        public decimal NormalizePayoutAmount(decimal amount) => amount;
    }
}
