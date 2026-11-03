using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Miningcore.Blockchain.Xelis.DaemonResponses;
using Miningcore.Contracts;
using Miningcore.Crypto;
using Miningcore.Crypto.Hashing.Algorithms;
using Miningcore.Extensions;
using Miningcore.Native;
using Miningcore.Stratum;
using Miningcore.Time;
using Miningcore.Util;

namespace Miningcore.Blockchain.Xelis;

public class XelisJob
{
    protected IMasterClock clock;
    protected double shareMultiplier;
    protected readonly IHashAlgorithm blake3Hasher = new Blake3();
    protected readonly IHashAlgorithm xelisHash = new XelisHash();
    protected readonly IHashAlgorithm xelisHashV2 = new XelisHashV2();
    protected readonly IHashAlgorithm xelisHashV3 = new XelisHashV3();

    protected readonly ConcurrentDictionary<string, bool> submissions = new(StringComparer.OrdinalIgnoreCase);
    public byte[] blockTargetBytes { get; protected set; }
    public BigInteger blockTargetValue { get; protected set; }   // U256::MAX / difficulty, as the daemon computes it
    protected byte[] prevHashBytes;
    protected byte[] versionBytes;
    protected byte[] heightBytes;
    protected byte[] nTimeBytes;
    protected byte[] extraNonceBytes;
    protected byte[] tipsCountBytes;
    protected byte[] tipsBytes;
    protected byte[] transactionsCountBytes;
    protected byte[] transactionsBytes;
    protected byte[] minerPublicKeyBytes;

    protected object[] jobParams;

    #region API-Surface

    public GetBlockHeaderResponse BlockHeader { get; protected set; }
    public GetBlockTemplateResponse BlockTemplate { get; protected set; }
    public string PrevHash { get; protected set; }
    public double Difficulty { get; protected set; }
    public string network { get; protected set; }

    public string JobId { get; protected set; }

    protected virtual byte[] SerializeCoinbase(byte[] extranonceBytes, byte[] nonceBytes)
    {
        using(var stream = new MemoryStream(XelisConstants.BlockWorkSize))
        {
            var bw = new BinaryWriter(stream);

            bw.Write(prevHashBytes);
            bw.Write(nTimeBytes);
            bw.Write(nonceBytes);
            bw.Write(extranonceBytes);
            bw.Write(minerPublicKeyBytes);
            
            return stream.ToArray();
        }
    }

    protected virtual byte[] SerializeHeader(byte[] extranonceBytes, byte[] nonceBytes)
    {
        using(var stream = new MemoryStream())
        {
            var bw = new BinaryWriter(stream);

            bw.Write(versionBytes);
            bw.Write(heightBytes);
            bw.Write(nTimeBytes);
            bw.Write(nonceBytes);
            bw.Write(extranonceBytes);
            bw.Write(tipsCountBytes);

            if(tipsBytes != null)
                bw.Write(tipsBytes);

            bw.Write(transactionsCountBytes);

            if(transactionsBytes != null)
                bw.Write(transactionsBytes);

            bw.Write(minerPublicKeyBytes);

            return stream.ToArray();
        }
    }

    /// <summary>
    /// hashlak: credited difficulty never exceeds the network difficulty. A static "d=" has no upper bound and a block
    /// candidate skips the ratio check, so a miner could be credited at any difficulty it picked; capped at the network
    /// difficulty every share pays exactly its expected work (review 2026-11-03, B2). Also keeps PPLNS scores finite.
    /// </summary>
    public static double CreditedDifficulty(double stratumDifficulty, double networkDifficulty)
    {
        return Math.Min(stratumDifficulty, networkDifficulty);
    }

    private static readonly Regex regexNonce = new($"^[0-9a-fA-F]{{{XelisConstants.NonceLength}}}$", RegexOptions.Compiled);

    /// <summary>
    /// hashlak: a nonce is exactly 16 hex characters, returned lowercase. HexToByteArray maps ANY character to a nibble,
    /// so "0q1r2s3t4u5v6g7h" and "1a2b3c4d5e6f7081" were the same nonce under two spellings while the duplicate check
    /// compared the raw strings: one valid share could be credited thousands of times (review 2026-11-03, B1).
    /// </summary>
    public static bool TryCanonicalNonce(string nonce, out string canonical)
    {
        canonical = null;

        if(string.IsNullOrEmpty(nonce) || !regexNonce.IsMatch(nonce))
            return false;

        canonical = nonce.ToLowerInvariant();
        return true;
    }

    protected virtual (Share Share, string BlockHeaderHex) ProcessShareInternal(
        StratumConnection worker, string nonce)
    {
        var context = worker.ContextAs<XelisWorkerContext>();
        var extranonceBytes = context.ExtraNonce1.HexToByteArray();
        var nonceBytes = nonce.HexToByteArray();

        var coinbaseBytes = SerializeCoinbase(extranonceBytes, nonceBytes);
        Span<byte> hashCoinbaseBytes = stackalloc byte[XelisConstants.HashSize];

        switch(BlockTemplate.Algorithm)
        {
            case XelisConstants.AlgorithmXelisHashV3:
                xelisHashV3.Digest(coinbaseBytes, hashCoinbaseBytes);
                break;
            case XelisConstants.AlgorithmXelisHashV2:
                xelisHashV2.Digest(coinbaseBytes, hashCoinbaseBytes);
                break;
            default:
                xelisHash.Digest(coinbaseBytes, hashCoinbaseBytes);
                break;
        }

        // hashlak: exact 256-bit value of the big-endian hash (NBitcoin's Target keeps only a compact mantissa)
        var hashCoinbaseBytesValue = new BigInteger(hashCoinbaseBytes, isUnsigned: true, isBigEndian: true);

        // calc share-diff
        var shareDiff = (double) new BigRational(XelisConstants.Diff1Target, BigInteger.Max(hashCoinbaseBytesValue, BigInteger.One)) * shareMultiplier;

        // diff check
        var stratumDifficulty = context.Difficulty;
        var ratio = shareDiff / stratumDifficulty;

        // check if the share meets the much harder block difficulty (block candidate)
        var isBlockCandidate = XelisUtils.MeetsTarget(hashCoinbaseBytes, blockTargetValue);

        // test if share meets at least workers current difficulty
        if(!isBlockCandidate && ratio < 0.99)
        {
            // check if share matched the previous difficulty from before a vardiff retarget
            if(context.VarDiff?.LastUpdate != null && context.PreviousDifficulty.HasValue)
            {
                ratio = shareDiff / context.PreviousDifficulty.Value;

                if(ratio < 0.99)
                    throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share [{shareDiff}]");

                // use previous difficulty
                stratumDifficulty = context.PreviousDifficulty.Value;
            }

            else
                throw new StratumException(StratumError.LowDifficultyShare, $"low difficulty share [{shareDiff}]");
        }

        var result = new Share
        {
            BlockHeight = (long) BlockHeader.TopoHeight,
            NetworkDifficulty = Difficulty,
            Difficulty = CreditedDifficulty(stratumDifficulty, Difficulty),
        };

        if(isBlockCandidate)
        {
            result.IsBlockCandidate = true;

            Span<byte> blockHashBytes = stackalloc byte[XelisConstants.HashSize];
            blake3Hasher.Digest(coinbaseBytes, blockHashBytes);

            result.BlockHash = blockHashBytes.ToHexString();

            // hashlak: the candidate header travels with this share; writing it into the job's shared BlockHeader let two
            // simultaneous candidates on one job submit each other's header (review 2026-11-03, S2)
            return (result, SerializeHeader(extranonceBytes, nonceBytes).ToHexString());
        }

        return (result, null);
    }

    public virtual void Init(GetBlockHeaderResponse blockHeader, GetBlockTemplateResponse blockTemplate, string jobId, IMasterClock clock, string network, double shareMultiplier, string prevHash)
    {
        Contract.RequiresNonNull(blockHeader);
        Contract.RequiresNonNull(blockTemplate);
        Contract.RequiresNonNull(clock);
        Contract.RequiresNonNull(network);
        Contract.Requires<ArgumentException>(!string.IsNullOrEmpty(jobId));
        Contract.RequiresNonNull(shareMultiplier);

        this.clock = clock;
        BlockHeader = blockHeader;
        BlockTemplate = blockTemplate;
        JobId = jobId;
        this.shareMultiplier = shareMultiplier;

        this.network = network;

        // hashlak: a template below difficulty 1 (or NaN) would make every hash a block and every PPLNS score infinite;
        // UpdateJob logs the exception and keeps the previous job
        if(!(BlockTemplate.Difficulty >= 1))
            throw new ArgumentException($"template difficulty {BlockTemplate.Difficulty} is invalid");

        Difficulty = BlockTemplate.Difficulty;
        blockTargetValue = XelisUtils.DifficultyToTargetValue(BlockTemplate.Difficulty);
        blockTargetBytes = XelisUtils.DifficultyToTarget(BlockTemplate.Difficulty);

        PrevHash = prevHash;
        prevHashBytes = PrevHash.HexToByteArray();

        var blockHeaderBytes = BlockHeader.Template.HexToByteArray();
        var blockTemplateBytes = BlockTemplate.Template.HexToByteArray();

        versionBytes = blockHeaderBytes.Take(XelisConstants.BlockHeaderOffsetHeight - XelisConstants.BlockHeaderOffsetVersion).ToArray();
        heightBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetHeight).Take(XelisConstants.BlockHeaderOffsetTimestamp - XelisConstants.BlockHeaderOffsetHeight).ToArray();

        //nTimeBytes = (BitConverter.IsLittleEndian ? BitConverter.GetBytes(XelisUtils.UnixTimeStamp(clock.Now)).ReverseInPlace() : BitConverter.GetBytes(XelisUtils.UnixTimeStamp(clock.Now))); // xelis_daemon expects a big endian format.
        nTimeBytes = blockTemplateBytes.Skip(XelisConstants.BlockTemplateOffsetTimestamp).Take(XelisConstants.BlockTemplateOffsetNonce - XelisConstants.BlockTemplateOffsetTimestamp).ToArray();

        extraNonceBytes = blockTemplateBytes.Skip(XelisConstants.BlockTemplateOffsetExtraNonce).Take(XelisConstants.BlockTemplateOffsetMinerPublicKey - XelisConstants.BlockTemplateOffsetExtraNonce).ToArray();

        tipsCountBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetTipsCount).Take(XelisConstants.BlockHeaderOffsetTips - XelisConstants.BlockHeaderOffsetTipsCount).ToArray();
        var tipsCount = uint.Parse(tipsCountBytes.ToHexString(), NumberStyles.HexNumber);
        if(tipsCount > 0)
            tipsBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetTips).Take(XelisConstants.HashSize * (int) tipsCount).ToArray();

        transactionsCountBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetTips + (XelisConstants.HashSize * (int) tipsCount)).Take(XelisConstants.BlockHeaderSizeTransactionsCount).ToArray();
        var transactionsCount = uint.Parse(transactionsCountBytes.ToHexString(), NumberStyles.HexNumber);
        if(transactionsCount > 0)
            transactionsBytes = blockHeaderBytes.Skip(XelisConstants.BlockHeaderOffsetTips + (XelisConstants.HashSize * (int) tipsCount) + XelisConstants.BlockHeaderSizeTransactionsCount).Take(XelisConstants.HashSize * (int) transactionsCount).ToArray();

        minerPublicKeyBytes = blockTemplateBytes.Skip(XelisConstants.BlockTemplateOffsetMinerPublicKey).Take(blockTemplateBytes.Length - XelisConstants.BlockTemplateOffsetMinerPublicKey).ToArray();

        jobParams = new object[]
        {
            JobId,
            nTimeBytes.ToHexString(),
            PrevHash,
            BlockTemplate.Algorithm,
            false
        };
    }

    public virtual object GetJobParams(bool isNew)
    {
        jobParams[^1] = isNew;
        return jobParams;
    }

    protected virtual bool RegisterSubmit(string extraNonce1, string nonce)
    {
        var key = new StringBuilder()
            .Append(extraNonce1)
            .Append(nonce)
            .ToString();

        return submissions.TryAdd(key, true);
    }

    public virtual (Share Share, string BlockHeaderHex) ProcessShare(StratumConnection worker,
        string nonce)
    {
        // validate nonce (a StratumException counts toward the invalid-share ban; an ArgumentException only disconnected)
        if(!TryCanonicalNonce(nonce, out var canonicalNonce))
            throw new StratumException(StratumError.Other, "invalid nonce");

        var context = worker.ContextAs<XelisWorkerContext>();

        // dupe check, on the canonical spelling
        if(!RegisterSubmit(context.ExtraNonce1, canonicalNonce))
            throw new StratumException(StratumError.DuplicateShare, "duplicate");

        return ProcessShareInternal(worker, canonicalNonce);
    }

    #endregion // API-Surface
}