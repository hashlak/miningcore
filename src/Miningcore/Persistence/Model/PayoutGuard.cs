namespace Miningcore.Persistence.Model;

/// <summary>
/// hashlak payout guard — status values shared by payout_runs and payout_run_items (payout_guard.sql CHECK constraints)
/// </summary>
public static class PayoutRunStatus
{
    public const string Reserved = "reserved";
    public const string Paid = "paid";
    public const string Failed = "failed";
    public const string Unresolved = "unresolved";

    public static readonly string[] Open = { Reserved, Unresolved };
}

public record PayoutGuardState
{
    public string PoolId { get; init; }
    public bool Tripped { get; init; }
    public string TrippedReason { get; init; }
    public DateTime? TrippedAt { get; init; }
    public DateTime? Day { get; init; }
    public decimal PaidToday { get; init; }
    public decimal ExpectedToday { get; init; }
    public decimal? WalletBalance { get; init; }
    public DateTime? WalletBalanceAt { get; init; }
    public DateTime Updated { get; init; }
}

public record PayoutRun
{
    public Guid RunId { get; init; }
    public string PoolId { get; init; }
    public string Coin { get; init; }
    public string Status { get; init; }
    public decimal AmountTotal { get; init; }
    public int PayeeCount { get; init; }
    public string Error { get; init; }
    public DateTime Created { get; init; }
    public DateTime Updated { get; init; }
}

public record PayoutRunItem
{
    public Guid RunId { get; init; }
    public string Address { get; init; }
    public decimal Amount { get; init; }
    public decimal? SentAmount { get; init; }
    public bool Recipient { get; init; }
    public string Status { get; init; }
    public string TxId { get; init; }
    public string Error { get; init; }
    public DateTime Updated { get; init; }
}
