using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reactive;
using System.Reactive.Subjects;
using System.Reflection;
using System.Security.Cryptography;
using Autofac;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Cryptonote;
using Miningcore.Blockchain.Progpow;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Miningcore.Time;
using Miningcore.Util;
using NBitcoin;
using Newtonsoft.Json;
using NLog;
using NSubstitute;
using Xunit;
using BlockTemplate = Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate;
using ILogger = NLog.ILogger;
using Share = Miningcore.Blockchain.Share;

namespace Miningcore.Tests.Blockchain;

/// <summary>
/// hashlak: review 2026-11-05. One piece of work is credited once, however the nonce is spelled or re-submitted.
/// </summary>
public class ShareDuplicateTests : TestBase
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
    public void Progpow_refuses_the_same_work_from_another_connection()
    {
        var job = new RecordingJob();

        WorkerJob(job).ProcessShare(Logger, Connection("rig-a"), Nonce, HeaderHash, MixHash);

        var ex = Assert.Throws<StratumException>(() => WorkerJob(job).ProcessShare(Logger, Connection("rig-b"), Nonce, HeaderHash, MixHash));
        Assert.Equal(StratumError.DuplicateShare, ex.Code);
        Assert.Single(job.Hashed);
    }

    private static ProgpowJobManager Manager() =>
        new(Substitute.For<IComponentContext>(), Substitute.For<IMasterClock>(), Substitute.For<IMessageBus>(), new FiroExtraNonceProvider("firo", 1));

    private static StratumConnection Fresh(string id) => Connection(id, null);

    private static string Subscribe(ProgpowJobManager manager, StratumConnection connection)
    {
        Assert.True(manager.TryGetSubscriberData(connection, out var data));
        return (string) data[0];
    }

    [Fact]
    public void A_connection_keeps_its_extranonce_on_a_second_subscribe()
    {
        var manager = Manager();
        var a = Fresh("a");

        Assert.Equal(Subscribe(manager, a), Subscribe(manager, a));
    }

    [Fact]
    public void No_two_live_connections_get_the_same_extranonce()
    {
        var manager = Manager();
        var seen = new HashSet<string>();

        for(var i = 0; i < 4096; i++)
            Assert.True(seen.Add(Subscribe(manager, Fresh($"c{i}"))));
    }

    [Fact]
    public void A_subscribe_is_refused_when_every_extranonce_is_held()
    {
        var manager = Manager();

        for(var i = 0; i < 4096; i++)
            Subscribe(manager, Fresh($"c{i}"));

        Assert.False(manager.TryGetSubscriberData(Fresh("one-too-many"), out _));
    }

    [Fact]
    public void KawPow_has_65536_extranonces_of_four_characters()
    {
        var provider = new RavencoinExtraNonceProvider("rvn", 1);
        var seen = new HashSet<string>();

        for(var i = 0; i < 65536; i++)
        {
            var value = provider.Next();
            Assert.Equal(4, value.Length);
            Assert.True(seen.Add(value));
        }

        Assert.Equal(65536UL, provider.Capacity);
    }

    [Fact]
    public void A_closed_connection_gives_its_extranonce_back()
    {
        var manager = Manager();
        var connections = Enumerable.Range(0, 4096).Select(i => Fresh($"c{i}")).ToArray();
        var extraNonces = connections.Select(c => Subscribe(manager, c)).ToArray();

        Terminate(connections[7]);

        Assert.Equal(extraNonces[7], Subscribe(manager, Fresh("new")));
    }

    [Fact]
    public void A_connection_closed_during_its_subscribe_gives_its_extranonce_back()
    {
        var manager = Manager();
        var connections = Enumerable.Range(0, 4095).Select(i => Fresh($"c{i}")).ToArray();

        foreach(var connection in connections)
            Subscribe(manager, connection);

        var closing = Fresh("closing");
        closing.IsAlive = false;
        var value = Subscribe(manager, closing);

        Assert.Equal(value, Subscribe(manager, Fresh("new")));
    }

    [Fact]
    public void A_lease_is_released_once_however_often_it_is_disposed()
    {
        var leases = new ExtraNonceLeases(new RavencoinExtraNonceProvider("rvn", 1));

        Assert.True(leases.TryLease(out var first, out var release));
        release.Dispose();

        // the value goes to someone else, and a second dispose of the old lease must not free it
        for(var i = 0; i < 65536; i++)
            Assert.True(leases.TryLease(out _, out _));

        release.Dispose();

        Assert.False(leases.TryLease(out _, out _));
        Assert.NotNull(first);
    }

    /// <summary>What StratumConnection's teardown does; it needs a live socket otherwise.</summary>
    private static void Terminate(StratumConnection connection)
    {
        connection.IsAlive = false;

        var terminated = (Subject<Unit>) typeof(StratumConnection)
            .GetField("terminated", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(connection);

        terminated!.OnNext(Unit.Default);
    }

    private class FixedResultCache : IProgpowCache
    {
        public byte[] SeedHash { get; set; } = new byte[32];

        public static byte[] Mix(byte[] header, ulong nonce) => SHA256.HashData(header.Concat(BitConverter.GetBytes(nonce)).ToArray());

        public bool Compute(ILogger logger, int blockNumber, byte[] hash, ulong nonce, out byte[] mixDigest, out byte[] result)
        {
            mixDigest = Mix(hash, nonce);
            result = new byte[32];
            result[31] = 1;   // meets any target: a block candidate
            return true;
        }

        public void Dispose() { }
    }

    private const string RealTemplate = """
        {"version":805306368,"previousBlockhash":"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b","coinbaseValue":125000000000,"target":"00000000000000ff000000000000000000000000000000000000000000000000","curTime":1665423220,"bits":"1a00ffff","height":3000000,"transactions":[],"coinbaseAux":{"flags":null}}
        """;

    [Fact]
    public void A_block_candidate_on_KawPow_is_credited_at_most_the_network_difficulty()
    {
        var coin = (ProgpowCoinTemplate) ModuleInitializer.CoinTemplates["ravencoin"];
        var template = JsonConvert.DeserializeObject<BlockTemplate>(RealTemplate, jsonSerializerSettings);
        var destination = BitcoinUtils.AddressToDestination("yNkA6gVSPqKzW6WmJtTazRLKbSkQA5ND2h", Network.TestNet);

        var job = new ProgpowJob();
        job.Init(template, "1", new PoolConfig { Template = coin }, null, new ClusterConfig(), MockMasterClock.FromTicks(638010200200475015),
            destination, Network.GetNetwork("testnet"), false, coin.ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue, new FixedResultCache());

        var connection = new StratumConnection(Logger, null, null, "c", false);
        connection.SetContext(new ProgpowWorkerContext { ExtraNonce1 = ExtraNonce, Difficulty = 1e30, Miner = "m" });

        var workerJob = new ProgpowWorkerJob("1", ExtraNonce);
        job.PrepareWorkerJob(workerJob, out var headerHash);

        var mix = FixedResultCache.Mix(headerHash.HexToByteArray(), Convert.ToUInt64(Nonce, 16)).ToHexString();
        var share = workerJob.ProcessShare(Logger, connection, Nonce, headerHash, mix).Share;

        Assert.True(share.IsBlockCandidate);
        Assert.Equal(share.NetworkDifficulty, share.Difficulty);
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
    [InlineData(10u, "aa", 10u, "aa", false)]
    [InlineData(10u, "aa", 11u, "bb", true)]    // next block
    [InlineData(10u, "aa", 10u, "bb", true)]    // same height, another tip: a reorg
    [InlineData(10u, "aa", null, null, true)]   // no current job
    public void Cryptonote_worker_job_is_stale_when_the_height_or_the_prev_hash_moved(uint height, string prevHash, uint? currentHeight, string currentPrevHash, bool stale)
    {
        Assert.Equal(stale, CryptonoteJobManager.IsStale(height, prevHash, currentHeight, currentPrevHash));
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
