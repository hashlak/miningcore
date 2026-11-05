using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Miningcore.Configuration;
using Miningcore.Mining;
using Miningcore.Payments;
using Miningcore.Payments.PaymentSchemes;
using Miningcore.Persistence;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

/// <summary>
/// A share stored with a difficulty far above the network's cannot take the whole PPLNS window, or stop the balance
/// update. Runs the real scheme against stub repositories.
/// </summary>
public class PPLNSCreditCapTests
{
    private const double Network = 1000d;
    private const decimal BlockReward = 100m;

    private readonly IConnectionFactory cf = Substitute.For<IConnectionFactory>();
    private readonly IDbConnection con = Substitute.For<IDbConnection>();
    private readonly IDbTransaction tx = Substitute.For<IDbTransaction>();
    private readonly IShareRepository shareRepo = Substitute.For<IShareRepository>();
    private readonly IBlockRepository blockRepo = Substitute.For<IBlockRepository>();
    private readonly IBalanceRepository balanceRepo = Substitute.For<IBalanceRepository>();
    private readonly IMiningPool pool = Substitute.For<IMiningPool>();
    private readonly IPayoutHandler handler = Substitute.For<IPayoutHandler>();
    private readonly Dictionary<string, decimal> credited = new();

    private static readonly DateTime Now = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);

    public PPLNSCreditCapTests()
    {
        cf.OpenConnectionAsync().Returns(Task.FromResult(con));
        pool.Config.Returns(new PoolConfig
        {
            Id = "p",
            PaymentProcessing = new PoolPaymentProcessingConfig()
        });

        handler.AdjustShareDifficulty(Arg.Any<double>()).Returns(x => x.Arg<double>());
        handler.FormatAmount(Arg.Any<decimal>()).Returns(x => x.Arg<decimal>().ToString());

        balanceRepo.AddAmountAsync(con, tx, "p", Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<string>(), Arg.Any<string[]>())
            .Returns(x =>
            {
                credited[x.ArgAt<string>(3)] = credited.GetValueOrDefault(x.ArgAt<string>(3)) + x.ArgAt<decimal>(4);
                return Task.FromResult(1);
            });
    }

    private Task RunAsync(params Share[] newestFirst)
    {
        shareRepo.ReadSharesBeforeAsync(con, "p", Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(newestFirst), Task.FromResult(Array.Empty<Share>()));

        var scheme = new PPLNSPaymentScheme(cf, shareRepo, blockRepo, balanceRepo);
        var block = new Block { BlockHeight = 1, Created = Now, Miner = "attacker" };

        return scheme.UpdateBalancesAsync(con, tx, pool, handler, block, BlockReward, CancellationToken.None);
    }

    private static Share S(string miner, double difficulty, int secondsAgo = 0) => new()
    {
        PoolId = "p",
        Miner = miner,
        Difficulty = difficulty,
        NetworkDifficulty = Network,
        Created = Now.AddSeconds(-secondsAgo)
    };

    [Theory]
    [InlineData(2000)]              // just above the network: took the whole window, silently
    [InlineData(1e15)]              // the same
    [InlineData(1e27)]              // above 1e21 the old diagnostics' FormatQuantity threw: balance update aborted
    [InlineData(1e30)]              // the same
    [InlineData(1e35)]              // the old score's decimal cast overflowed
    public async Task Huge_share_is_paid_as_one_block_of_work(double difficulty)
    {
        // default window = 2 blocks of work; newest first: the attacker's share is capped at 1, older honest work gets the rest
        await RunAsync(S("attacker", difficulty), S("honest", Network, 1));

        Assert.Equal(50m, credited["attacker"]);
        Assert.Equal(50m, credited["honest"]);
    }

    [Fact]
    public async Task Newer_honest_work_keeps_its_share_of_the_window()
    {
        await RunAsync(S("honest", Network), S("honest", Network / 2, 1), S("attacker", 1e27, 2));

        Assert.Equal(75m, credited["honest"]);
        Assert.Equal(25m, credited["attacker"]);
    }

    [Fact]
    public async Task A_failing_discard_log_does_not_stop_the_credits()
    {
        var calls = 0;
        shareRepo.ReadSharesBeforeAsync(con, "p", Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => calls++ == 0
                ? Task.FromResult(new[] { S("a", Network), S("b", Network, 1) })
                : throw new IndexOutOfRangeException());
        shareRepo.CountSharesBeforeAsync(con, tx, "p", Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(1L));

        var scheme = new PPLNSPaymentScheme(cf, shareRepo, blockRepo, balanceRepo);
        await scheme.UpdateBalancesAsync(con, tx, pool, handler, new Block { BlockHeight = 1, Created = Now, Miner = "a" }, BlockReward, CancellationToken.None);

        Assert.Equal(2, calls);   // the rewards page, then the discard log
        Assert.Equal(50m, credited["a"]);
        Assert.Equal(50m, credited["b"]);
    }

    private void ReadPages(Func<int, Share[]> read)
    {
        var calls = 0;
        shareRepo.ReadSharesBeforeAsync(con, "p", Arg.Any<DateTime>(), Arg.Any<bool>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(read(calls++)));
        shareRepo.CountSharesBeforeAsync(con, tx, "p", Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(1L));
    }

    private Task Update(CancellationToken ct) =>
        new PPLNSPaymentScheme(cf, shareRepo, blockRepo, balanceRepo)
            .UpdateBalancesAsync(con, tx, pool, handler, new Block { BlockHeight = 1, Created = Now, Miner = "a" }, BlockReward, ct);

    [Fact]
    public async Task A_failing_discard_log_still_prunes()
    {
        ReadPages(c => c == 0 ? new[] { S("a", Network), S("b", Network, 1) } : throw new IndexOutOfRangeException());

        await Update(CancellationToken.None);

        await shareRepo.Received(1).DeleteSharesBeforeAsync(con, tx, "p", Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_in_the_discard_log_aborts_the_update()
    {
        ReadPages(c => c == 0 ? new[] { S("a", Network), S("b", Network, 1) } : throw new OperationCanceledException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Update(CancellationToken.None));
    }

    [Fact]
    public async Task Cancellation_between_share_pages_does_not_commit_part_of_a_block()
    {
        using var cts = new CancellationTokenSource();
        var page = Enumerable.Range(0, 50000).Select(i => S("a", Network / 200000, i)).ToArray();

        ReadPages(_ =>
        {
            cts.Cancel();
            return page;
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Update(cts.Token));
        Assert.Empty(credited);
    }

    [Fact]
    public async Task Ordinary_shares_are_paid_as_before()
    {
        await RunAsync(S("a", Network / 2), S("b", Network / 2, 1), S("c", Network, 2));

        Assert.Equal(25m, credited["a"]);
        Assert.Equal(25m, credited["b"]);
        Assert.Equal(50m, credited["c"]);
    }
}
