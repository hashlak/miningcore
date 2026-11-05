namespace Miningcore.Mining;

/// <summary>
/// hashlak: how much work a share is credited with. No difficulty a miner picks can pay more than the work it proves.
/// </summary>
public static class ShareCredit
{
    /// <summary>
    /// Credited difficulty never exceeds the network difficulty. A static "d=" has no upper bound of its own and a
    /// block candidate skips the job's ratio check, so a miner could otherwise be credited at any difficulty it picked.
    /// At the cap a candidate pays exactly its expected work: one block. Invalid inputs credit nothing.
    /// </summary>
    public static double Difficulty(double stratumDifficulty, double networkDifficulty)
    {
        if(!(stratumDifficulty > 0) || !double.IsFinite(networkDifficulty) || networkDifficulty <= 0)
            return 0;

        return Math.Min(stratumDifficulty, networkDifficulty);
    }

    /// <summary>
    /// PPLNS score of a stored share, at most 1. The layer under the jobs' cap: it also covers rows written before the
    /// cap existed, and it keeps the decimal cast from overflowing, which would abort the balance update of every block.
    /// </summary>
    public static decimal Score(double adjustedDifficulty, double networkDifficulty)
    {
        var credited = Difficulty(adjustedDifficulty, networkDifficulty);

        return credited > 0 ? (decimal) (credited / networkDifficulty) : 0m;
    }

    /// <summary>
    /// A static "d=" stays within the port's maxDiff. A miner above the network difficulty never submits the blocks
    /// between the network difficulty and its own, so the pool loses them; maxDiff is set well below the network's.
    /// </summary>
    public static double StaticDifficulty(double requested, double? maxDiff)
    {
        return maxDiff.HasValue ? Math.Min(requested, maxDiff.Value) : requested;
    }
}
