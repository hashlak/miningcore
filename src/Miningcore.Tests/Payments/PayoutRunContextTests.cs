using System;
using System.Collections.Generic;
using System.Data;
using System.Threading.Tasks;
using Miningcore.Payments;
using Miningcore.Persistence.Model;
using Miningcore.Persistence.Repositories;
using NSubstitute;
using Xunit;

namespace Miningcore.Tests.Payments;

public class PayoutRunContextTests
{
    private readonly IPayoutGuardRepository repo = Substitute.For<IPayoutGuardRepository>();
    private readonly IDbConnection con = Substitute.For<IDbConnection>();
    private readonly IDbTransaction tx = Substitute.For<IDbTransaction>();
    private readonly Guid runId = Guid.NewGuid();

    private static Balance B(string address, decimal amount) => new() { PoolId = "p", Address = address, Amount = amount };

    private PayoutRunContext Context() => new(runId, "p", new[] { B("a", 5), B("b", 7) }, repo);

    [Fact]
    public async Task Confirm_Marks_Items_Paid()
    {
        var ctx = Context();
        repo.MarkItemsPaidAsync(con, tx, runId, Arg.Any<IReadOnlyList<(string, string, decimal)>>()).Returns(2);

        await ctx.ConfirmAsync(con, tx, new[] { ("a", "tx1", 5m), ("b", "tx1", 7m) });

        var snapshot = ctx.Snapshot();
        Assert.Equal(PayoutItemState.Paid, snapshot["a"].State);
        Assert.Equal("tx1", snapshot["b"].Detail);
    }

    [Fact]
    public async Task Confirm_Is_Idempotent_For_The_Same_Txid_But_Not_Another()
    {
        var ctx = Context();
        repo.MarkItemsPaidAsync(con, tx, runId, Arg.Any<IReadOnlyList<(string, string, decimal)>>()).Returns(1);

        await ctx.ConfirmAsync(con, tx, new[] { ("a", "tx1", 5m) });
        await ctx.ConfirmAsync(con, tx, new[] { ("a", "tx1", 5m) });   // persistence retry replays the same confirmation

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ConfirmAsync(con, tx, new[] { ("a", "tx2", 5m) }));
    }

    [Fact]
    public async Task Confirm_Rejects_Unknown_Address_Empty_Txid_And_Row_Mismatch()
    {
        var ctx = Context();

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ConfirmAsync(con, tx, new[] { ("zzz", "tx1", 1m) }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ConfirmAsync(con, tx, new[] { ("a", "", 5m) }));

        repo.MarkItemsPaidAsync(con, tx, runId, Arg.Any<IReadOnlyList<(string, string, decimal)>>()).Returns(0);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ConfirmAsync(con, tx, new[] { ("a", "tx1", 5m) }));

        Assert.Equal(PayoutItemState.Reserved, ctx.Snapshot()["a"].State);
    }

    [Fact]
    public async Task Confirm_After_Reject_Throws()
    {
        var ctx = Context();
        ctx.Reject(new[] { B("a", 5) }, "bad address");

        await Assert.ThrowsAsync<InvalidOperationException>(() => ctx.ConfirmAsync(con, tx, new[] { ("a", "tx1", 5m) }));
        await repo.DidNotReceiveWithAnyArgs().MarkItemsPaidAsync(default, default, default, default);
    }

    [Fact]
    public void First_Verdict_Wins_And_Strangers_Are_Ignored()
    {
        var ctx = Context();

        ctx.Reject(new[] { B("a", 5) }, "rejected");
        ctx.Unresolved(new[] { B("a", 5), B("b", 7), B("stranger", 1) }, "timeout");

        var snapshot = ctx.Snapshot();
        Assert.Equal((PayoutItemState.Failed, "rejected"), snapshot["a"]);
        Assert.Equal((PayoutItemState.Unresolved, "timeout"), snapshot["b"]);
        Assert.False(snapshot.ContainsKey("stranger"));
        Assert.Equal(12m, ctx.Total);
    }
}
