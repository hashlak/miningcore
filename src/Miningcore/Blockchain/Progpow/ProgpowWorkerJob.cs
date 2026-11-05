using System.Globalization;
using Miningcore.Mining;
using Miningcore.Stratum;
using NLog;
using Contract = Miningcore.Contracts.Contract;

namespace Miningcore.Blockchain.Progpow;

public class ProgpowWorkerJob
{
    private const int NonceLength = 16;

    public ProgpowWorkerJob(string jobId, string extraNonce1)
    {
        Id = jobId;
        ExtraNonce1 = extraNonce1;
    }

    public string Id { get; }
    public ProgpowJob Job { get; set; }
    public uint Height { get; set; }
    public string ExtraNonce1 { get; set; }
    public string Bits { get; set; }
    public string SeedHash { get; set; }

    public (Share Share, string BlockHex) ProcessShare(ILogger logger, StratumConnection worker, string nonce, string headerHash, string mixHash)
    {
        Contract.RequiresNonNull(worker);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(nonce));

        var context = worker.ContextAs<ProgpowWorkerContext>();

        // mixHash
        if(mixHash.Length != 64)
            throw new StratumException(StratumError.Other, $"incorrect size of mixHash: {mixHash}");

        // validate nonce
        if(!NonceFormat.TryCanonicalHex(nonce, NonceLength, out var canonicalNonce))
            throw new StratumException(StratumError.Other, "invalid nonce");

        // check if nonce is within range
        if(!canonicalNonce.StartsWith(context.ExtraNonce1[0..4], StringComparison.OrdinalIgnoreCase))
            throw new StratumException(StratumError.Other, $"nonce out of range: {canonicalNonce}");

        // dupe check, per job: a retarget creates a new worker job on the same job
        if(!Job.RegisterSubmit(worker.ConnectionId, canonicalNonce))
            throw new StratumException(StratumError.DuplicateShare, "duplicate share");

        var nonceLong = ulong.Parse(canonicalNonce, NumberStyles.HexNumber);

        return Job.ProcessShareInternal(logger, worker, nonceLong, headerHash, mixHash);
    }
}