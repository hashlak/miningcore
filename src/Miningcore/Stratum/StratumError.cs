namespace Miningcore.Stratum;

public enum StratumError
{
    Other = 20,
    JobNotFound = 21, // stale
    DuplicateShare = 22,
    LowDifficultyShare = 23,
    UnauthorizedWorker = 24,
    NotSubscribed = 25,
    MinusOne = -1
}

public class StratumException : Exception
{
    public StratumException(StratumError code, string message) : base(message)
    {
        Code = code;
    }

    public StratumError Code { get; set; }
}

/// <summary>
/// hashlak: a share for a job the pool no longer holds for this connection (rotated out, or its block is gone). Late work
/// from an honest miner on a slow line looks exactly like this, so it is rejected on the wire but never feeds the ban ratio.
/// </summary>
public class StaleShareException : StratumException
{
    public StaleShareException(StratumError code, string message) : base(code, message)
    {
    }
}
