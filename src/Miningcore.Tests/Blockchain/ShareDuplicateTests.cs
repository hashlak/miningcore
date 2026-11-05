using System.Collections.Generic;
using System.Globalization;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Progpow;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Util;
using NLog;
using Xunit;

namespace Miningcore.Tests.Blockchain;

/// <summary>
/// hashlak: review 2026-11-05. One piece of work is credited once, however the nonce is spelled or re-submitted.
/// </summary>
public class ShareDuplicateTests
{
    private const string ExtraNonce = "abcd";
    private const string Nonce = "abcd12345678ef01";
    private const string HeaderHash = "header";
    private static readonly string MixHash = new('0', 64);
    private static readonly ILogger Logger = new NullLogger(LogManager.LogFactory);

    /// <summary>The share path up to the hash, which needs the native libraries; records what reached it.</summary>
    private class RecordingJob : ProgpowJob
    {
        public readonly List<ulong> Hashed = new();

        public override (Share Share, string BlockHex) ProcessShareInternal(ILogger logger, StratumConnection worker, ulong nonce, string inputHeaderHash, string mixHash)
        {
            Hashed.Add(nonce);
            return (new Share(), null);
        }
    }

    private static StratumConnection Connection(string id, string extraNonce = ExtraNonce)
    {
        var connection = new StratumConnection(Logger, null, null, id, false);
        var context = new ProgpowWorkerContext { ExtraNonce1 = extraNonce };
        connection.SetContext(context);
        return connection;
    }

    private static ProgpowWorkerJob WorkerJob(RecordingJob job, string id = "1") => new(id, ExtraNonce) { Job = job };

    [Theory]
    [InlineData("abcd12345678    ")]     // ulong.Parse with HexNumber ignores trailing whitespace
    [InlineData("abcd12345678\t\t\t\t")]
    [InlineData("abcd12345678\n\n\n\n")]
    [InlineData("abcd12345678\r \t\n")]
    [InlineData("abcd12345678\v\f  ")]
    [InlineData("abcd1234567g0000")]
    [InlineData("abcd1234567")]
    [InlineData("abcd12345678ef012")]
    [InlineData("0xabcd12345678ef")]
    public void Progpow_refuses_every_other_spelling_of_a_nonce(string nonce)
    {
        var job = new RecordingJob();

        var ex = Assert.Throws<StratumException>(() => WorkerJob(job).ProcessShare(Logger, Connection("c"), nonce, HeaderHash, MixHash));

        Assert.Equal(StratumError.Other, ex.Code);
        Assert.Empty(job.Hashed);
    }

    [Fact]
    public void The_padded_spelling_really_was_the_same_nonce()
    {
        Assert.Equal(0xabcd12345678UL, ulong.Parse("abcd12345678    ", NumberStyles.HexNumber));
        Assert.Equal(0xabcd12345678UL, ulong.Parse("abcd12345678\n\n\n\n", NumberStyles.HexNumber));
    }

    [Fact]
    public void Progpow_counts_upper_and_lower_case_as_one_nonce()
    {
        var job = new RecordingJob();
        var connection = Connection("c");
        var workerJob = WorkerJob(job);

        workerJob.ProcessShare(Logger, connection, Nonce, HeaderHash, MixHash);

        var ex = Assert.Throws<StratumException>(() => workerJob.ProcessShare(Logger, connection, Nonce.ToUpperInvariant(), HeaderHash, MixHash));
        Assert.Equal(StratumError.DuplicateShare, ex.Code);
        Assert.Single(job.Hashed);
    }

    [Fact]
    public void Progpow_refuses_a_replay_on_a_new_worker_job_of_the_same_job()
    {
        var job = new RecordingJob();
        var connection = Connection("c");

        WorkerJob(job, "1").ProcessShare(Logger, connection, Nonce, HeaderHash, MixHash);

        // a vardiff retarget or a second subscribe creates a new worker job on the same job
        var ex = Assert.Throws<StratumException>(() => WorkerJob(job, "2").ProcessShare(Logger, connection, Nonce, HeaderHash, MixHash));
        Assert.Equal(StratumError.DuplicateShare, ex.Code);
        Assert.Single(job.Hashed);
    }

    [Fact]
    public void Progpow_does_not_let_two_rigs_on_one_extranonce_refuse_each_other()
    {
        var job = new RecordingJob();

        WorkerJob(job).ProcessShare(Logger, Connection("rig-a"), Nonce, HeaderHash, MixHash);
        WorkerJob(job).ProcessShare(Logger, Connection("rig-b"), Nonce, HeaderHash, MixHash);

        Assert.Equal(2, job.Hashed.Count);
    }

    [Fact]
    public void Progpow_accepts_the_same_nonce_on_a_new_job()
    {
        var first = new RecordingJob();
        var second = new RecordingJob();
        var connection = Connection("c");

        WorkerJob(first).ProcessShare(Logger, connection, Nonce, HeaderHash, MixHash);
        WorkerJob(second).ProcessShare(Logger, connection, Nonce, HeaderHash, MixHash);

        Assert.Single(second.Hashed);
    }

    [Fact]
    public void Progpow_refuses_a_nonce_outside_the_connections_range()
    {
        var ex = Assert.Throws<StratumException>(() => WorkerJob(new RecordingJob()).ProcessShare(Logger, Connection("c", "ffff"), Nonce, HeaderHash, MixHash));

        Assert.Equal(StratumError.Other, ex.Code);
    }

    [Theory]
    [InlineData("deadbeef", true)]
    [InlineData("deadbeef\n", false)]     // "$" alone accepted this; it decodes to the same four bytes
    [InlineData("\ndeadbeef", false)]
    [InlineData("deadbeef ", false)]
    [InlineData("DEADBEEF", false)]
    [InlineData("deadbee", false)]
    [InlineData("deadbeef0", false)]
    [InlineData("deadbeeg", false)]
    public void Cryptonote_accepts_one_spelling_of_a_nonce(string nonce, bool valid)
    {
        Assert.Equal(valid, CryptonoteConstants.RegexValidNonce.IsMatch(nonce));
    }

    [Theory]
    [InlineData(null, 8)]
    [InlineData("", 8)]
    [InlineData("deadbee", 8)]
    [InlineData("deadbeef\n", 9)]
    [InlineData("١eadbeef", 8)]        // Arabic-Indic digit: not ASCII hex
    public void NonceFormat_refuses(string nonce, int length)
    {
        Assert.False(NonceFormat.TryCanonicalHex(nonce, length, out var canonical));
        Assert.Null(canonical);
    }

    [Fact]
    public void NonceFormat_lowercases()
    {
        Assert.True(NonceFormat.TryCanonicalHex("DEADbeef", 8, out var canonical));
        Assert.Equal("deadbeef", canonical);
    }

    [Theory]
    [InlineData(999, "K")]
    [InlineData(1500000, "M")]
    [InlineData(1e30, "E")]                         // threw: ran past the units
    [InlineData(double.PositiveInfinity, "E")]      // never returned
    [InlineData(double.NaN, "K")]
    public void FormatQuantity_stays_inside_its_units(double value, string unit)
    {
        Assert.EndsWith(unit, FormatUtil.FormatQuantity(value));
    }
}
