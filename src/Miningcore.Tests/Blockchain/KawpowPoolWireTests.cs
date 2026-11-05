using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reactive.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autofac;
using AutoMapper;
using Microsoft.IO;
using Miningcore.Banning;
using Miningcore.Blockchain;
using Miningcore.Blockchain.Bitcoin;
using Miningcore.Blockchain.Progpow;
using Miningcore.Configuration;
using Miningcore.Crypto.Hashing.Progpow;
using Miningcore.Extensions;
using Miningcore.Messaging;
using Miningcore.Mining;
using Miningcore.Nicehash;
using Miningcore.Persistence;
using Miningcore.Persistence.Repositories;
using Miningcore.Rpc;
using Miningcore.Stratum;
using Miningcore.Tests.Util;
using Miningcore.Time;
using NBitcoin;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NLog;
using NSubstitute;
using Xunit;
using BlockTemplate = Miningcore.Blockchain.Bitcoin.DaemonResponses.BlockTemplate;
using IBlockRepository = Miningcore.Persistence.Repositories.IBlockRepository;
using ILogger = NLog.ILogger;

namespace Miningcore.Tests.Blockchain;

/// <summary>
/// hashlak: a real ProgpowPool over loopback sockets, with only the native KawPow hash and the daemon stubbed.
/// Covers what a miner sees on the wire. Review 2026-11-05.
/// </summary>
public class KawpowPoolWireTests : TestBase
{
    private const string Template = """
        {"version":805306368,"previousBlockhash":"0000011a86a1ad3609e5359b6b6411a1654108ee7c1afc003dec23b5a0400e4b","coinbaseValue":125000000000,"target":"00000000000000ff000000000000000000000000000000000000000000000000","curTime":1665423220,"bits":"1a00ffff","height":3000000,"transactions":[],"coinbaseAux":{"flags":null}}
        """;

    private class Cache : IProgpowCache
    {
        public byte[] SeedHash { get; set; } = new byte[32];

        public static byte[] Mix(byte[] header, ulong nonce) => SHA256.HashData(header.Concat(BitConverter.GetBytes(nonce)).ToArray());

        public bool Compute(ILogger logger, int blockNumber, byte[] hash, ulong nonce, out byte[] mixDigest, out byte[] result)
        {
            mixDigest = Mix(hash, nonce);
            result = "00000000ff000000000000000000000000000000000000000000000000000000".HexToByteArray();   // difficulty 1
            return true;
        }

        public void Dispose() { }
    }

    private class TinyProvider : IExtraNonceProvider
    {
        public TinyProvider(ulong capacity) => Capacity = capacity;

        private ulong next;

        public int ByteSize => 2;
        public ulong Capacity { get; }
        public string Next() => (next++ % Capacity).ToString("x4");
    }

    private class Manager : ProgpowJobManager
    {
        public Manager(IComponentContext ctx, IMasterClock clock, IMessageBus bus, IExtraNonceProvider provider) : base(ctx, clock, bus, provider) { }

        public override Task<AddressValidation> ValidateAddressDetailedAsync(string address, CancellationToken ct) => Task.FromResult(AddressValidation.Valid);

        public void Init(PoolConfig pc, ClusterConfig cc, ProgpowJob job)
        {
            poolConfig = pc;
            clusterConfig = cc;
            currentJob = job;
            logger = LogManager.GetLogger("test");
        }

        public ProgpowJob Current { set => currentJob = value; }
        public object Params() => GetJobParamsForStratum(false);

        public void StartWithDeadDaemon(RpcClient client, CancellationToken ct)
        {
            rpc = client;
            hasInitialBlockTemplate = true;
            SetupJobUpdates(ct);
        }
    }

    private class Pool : ProgpowPool
    {
        public Pool(IComponentContext ctx, JsonSerializerSettings settings, IConnectionFactory cf, IStatsRepository stats, IMapper mapper, IMasterClock clock, IMessageBus bus, RecyclableMemoryStreamManager rmsm, NicehashService nicehash)
            : base(ctx, settings, cf, stats, mapper, clock, bus, rmsm, nicehash) { }

        public TimeSpan UnauthorizedLimit = TimeSpan.FromSeconds(60);

        protected override TimeSpan UnauthorizedTimeout => UnauthorizedLimit;

        public Task Serve(CancellationToken ct, StratumEndpoint endpoint) => RunAsync(ct, new[] { endpoint });
        public Task Broadcast(object job) => OnNewJobAsync(job);
        public StratumConnection Connection(int index) => connections.Values.ElementAt(index);
        public void SetBanManager(IBanManager manager) => banManager = manager;
        public Task IdleVarDiff(StratumConnection connection) => UpdateVarDiffAsync(connection, true, CancellationToken.None);
    }

    private sealed class Rig : IDisposable
    {
        public Pool Pool;
        public Manager Manager;
        public PoolConfig Config;
        public int Port;
        public IBanManager Bans = Substitute.For<IBanManager>();
        public CancellationTokenSource Cts = new();
        public Func<int, ProgpowJob> NewJob;

        public int BanCalls => Bans.ReceivedCalls().Count(c => c.GetMethodInfo().Name == "Ban");

        public void Dispose() => Cts.Cancel();
    }

    private Rig NewRig(int clientTimeout = 600, IExtraNonceProvider provider = null)
    {
        var rig = new Rig();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        rig.Port = ((IPEndPoint) listener.LocalEndpoint).Port;
        listener.Stop();

        var coin = (ProgpowCoinTemplate) ModuleInitializer.CoinTemplates["ravencoin"];
        var varDiff = new VarDiffConfig { MinDiff = 0.01, TargetTime = 15, RetargetTime = 90, VariancePercent = 30 };
        var endpoint = new PoolEndpoint { ListenAddress = "127.0.0.1", Difficulty = 0.02, VarDiff = varDiff, Name = "test" };

        rig.Config = new PoolConfig
        {
            Id = "rvn", Coin = "ravencoin", Template = coin, EnableInternalStratum = true, ClientConnectionTimeout = clientTimeout,
            Ports = new Dictionary<int, PoolEndpoint> { [rig.Port] = endpoint }, JobRebroadcastTimeout = 1, Daemons = Array.Empty<DaemonEndpointConfig>(),
            Banning = new PoolShareBasedBanningConfig { Enabled = true, Time = 600, InvalidPercent = 50, CheckThreshold = 50 }
        };

        var cluster = new ClusterConfig
        {
            ClusterName = "test", Logging = new ClusterLoggingConfig(),
            Banning = new ClusterBanningConfig { BanOnInvalidShares = true, BanOnLoginFailure = true, BanOnJunkReceive = true, LoginFailureBanTime = 3 }
        };

        var builder = new ContainerBuilder();
        builder.RegisterInstance(Substitute.For<IBlockRepository>()).As<IBlockRepository>();
        builder.RegisterInstance(Substitute.For<IShareRepository>()).As<IShareRepository>();
        var container = builder.Build();

        var clock = new StandardClock();
        var bus = new MessageBus();

        rig.NewJob = seconds =>
        {
            var template = JsonConvert.DeserializeObject<BlockTemplate>(Template, jsonSerializerSettings);
            var destination = BitcoinUtils.AddressToDestination("yNkA6gVSPqKzW6WmJtTazRLKbSkQA5ND2h", Network.TestNet);
            var job = new ProgpowJob();

            job.Init(template, "1", new PoolConfig { Template = coin }, null, new ClusterConfig(), MockMasterClock.FromTicks(638010200200475015 + seconds * TimeSpan.TicksPerSecond),
                destination, Network.GetNetwork("testnet"), false, coin.ShareMultiplier, coin.CoinbaseHasherValue, coin.HeaderHasherValue, coin.BlockHasherValue, new Cache());

            return job;
        };

        rig.Pool = new Pool(container, jsonSerializerSettings, Substitute.For<IConnectionFactory>(), Substitute.For<IStatsRepository>(), Substitute.For<IMapper>(), clock, bus,
            new RecyclableMemoryStreamManager(), (NicehashService) RuntimeHelpers.GetUninitializedObject(typeof(NicehashService)));

        rig.Pool.Configure(rig.Config, cluster);
        rig.Pool.SetBanManager(rig.Bans);

        rig.Manager = new Manager(container, clock, bus, provider ?? new RavencoinExtraNonceProvider("rvn", 1));
        rig.Manager.Init(rig.Config, cluster, rig.NewJob(0));

        typeof(ProgpowPool).GetField("manager", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(rig.Pool, rig.Manager);

        rig.Pool.Broadcast(rig.Manager.Params()).GetAwaiter().GetResult();
        _ = rig.Pool.Serve(rig.Cts.Token, new StratumEndpoint(new IPEndPoint(IPAddress.Loopback, rig.Port), endpoint));

        return rig;
    }

    private sealed class Client : IDisposable
    {
        private readonly TcpClient tcp = new();
        private NetworkStream stream;
        private int id = 100;

        public readonly List<JObject> Log = new();
        public volatile bool Closed;

        public async Task Connect(int port)
        {
            await tcp.ConnectAsync(IPAddress.Loopback, port);
            stream = tcp.GetStream();

            _ = Task.Run(async () =>
            {
                try
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, true);

                    while(await reader.ReadLineAsync() is { } line)
                        lock(Log) Log.Add(JObject.Parse(line));
                }

                catch(IOException) { }
                catch(ObjectDisposedException) { }

                Closed = true;
            });
        }

        public async Task<int> Send(string method, params object[] parameters)
        {
            var requestId = Interlocked.Increment(ref id);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(new { id = requestId, method, @params = parameters }) + "\n"));
            return requestId;
        }

        public async Task<JObject> Response(int requestId, int ms = 3000)
        {
            var sw = Stopwatch.StartNew();

            while(sw.ElapsedMilliseconds < ms && !Closed)
            {
                lock(Log)
                {
                    var match = Log.FirstOrDefault(x => x["id"]?.Type == JTokenType.Integer && (int) x["id"] == requestId);

                    if(match != null)
                        return match;
                }

                await Task.Delay(5);
            }

            return null;
        }

        public JObject[] Notifies(string method)
        {
            lock(Log)
                return Log.Where(x => (string) x["method"] == method).ToArray();
        }

        public int Count
        {
            get { lock(Log) return Log.Count; }
        }

        public void Dispose() => tcp.Close();
    }

    private static async Task<bool> Until(Func<bool> condition, int ms = 5000)
    {
        var sw = Stopwatch.StartNew();

        while(sw.ElapsedMilliseconds < ms)
        {
            if(condition())
                return true;

            await Task.Delay(10);
        }

        return condition();
    }

    private static async Task<(Client Client, string ExtraNonce)> Subscribe(Rig rig)
    {
        var client = new Client();
        await client.Connect(rig.Port);

        var response = await client.Response(await client.Send("mining.subscribe", "kawpowminer/1.2.3"));

        return (client, (string) response?["result"]?[1]);
    }

    private static async Task<(Client Client, string ExtraNonce)> Miner(Rig rig)
    {
        var (client, extraNonce) = await Subscribe(rig);
        await client.Response(await client.Send("mining.authorize", "Rgood.rig1", "x"));

        return (client, extraNonce);
    }

    private static Task<int> SubmitValid(Client client, string extraNonce, ulong low)
    {
        var job = client.Notifies("mining.notify").Last();
        var header = (string) job["params"][1];
        var nonce = extraNonce + low.ToString("x12");
        var mix = Cache.Mix(header.HexToByteArray(), ulong.Parse(nonce, NumberStyles.HexNumber)).ToHexString();

        return client.Send("mining.submit", "Rgood.rig1", (string) job["params"][0], "0x" + nonce, "0x" + header, "0x" + mix);
    }

    [Fact]
    public async Task Only_accepted_shares_keep_a_connection_alive()
    {
        using var rig = NewRig(clientTimeout: 3);
        var (junk, _) = await Miner(rig);
        var (honest, extraNonce) = await Miner(rig);

        for(var i = 1; i <= 6; i++)
        {
            if(!junk.Closed)
                await junk.Send("mining.submit", "Rgood.rig1", "no-such-job", "0x0", "0x0", "0x0");

            var response = await honest.Response(await SubmitValid(honest, extraNonce, (ulong) i));
            Assert.True((bool) response!["result"]);

            await Task.Delay(1000);
            await rig.Pool.Broadcast(rig.Manager.Params());
        }

        Assert.True(await Until(() => junk.Closed, 1000));
        Assert.False(honest.Closed);
    }

    [Fact]
    public async Task A_retarget_waits_for_the_next_job_and_arrives_with_it()
    {
        using var rig = NewRig();
        var (client, _) = await Miner(rig);

        var connection = rig.Pool.Connection(0);
        var context = connection.ContextAs<ProgpowWorkerContext>();
        var oldDifficulty = context.Difficulty;
        context.VarDiff.LastTs = (DateTime.UtcNow - TimeSpan.FromSeconds(1000)).ToUnixSeconds();

        await rig.Pool.IdleVarDiff(connection);
        var before = client.Count;
        await Task.Delay(300);

        Assert.True(context.HasPendingDifficulty);
        Assert.Equal(oldDifficulty, context.Difficulty);
        Assert.Equal(before, client.Count);   // nothing on the wire until a job

        rig.Manager.Current = rig.NewJob(1);
        await rig.Pool.Broadcast(rig.Manager.Params());
        Assert.True(await Until(() => client.Count >= before + 2));

        JObject[] sent;
        lock(client.Log)
            sent = client.Log.Skip(before).ToArray();

        Assert.Equal("mining.set_target", (string) sent[0]["method"]);
        Assert.Equal("mining.notify", (string) sent[1]["method"]);
        Assert.Equal((string) sent[0]["params"][0], (string) sent[1]["params"][3]);   // the job carries the new target
        Assert.NotEqual(oldDifficulty, context.Difficulty);
        Assert.False(context.HasPendingDifficulty);
    }

    [Fact]
    public async Task A_full_pool_closes_the_socket_without_an_error_and_without_a_ban()
    {
        using var rig = NewRig(provider: new TinyProvider(1));
        var (first, _) = await Subscribe(rig);

        var second = new Client();
        await second.Connect(rig.Port);
        await second.Send("mining.subscribe", "kawpowminer/1.2.3");

        Assert.True(await Until(() => second.Closed));
        Assert.Equal(0, second.Count);
        Assert.Equal(0, rig.BanCalls);
        Assert.False(first.Closed);
    }

    [Fact]
    public async Task Stale_shares_never_ban_but_invalid_shares_still_do()
    {
        using var rig = NewRig();
        var (late, _) = await Miner(rig);

        // well past checkThreshold (50) and 100 % of them rejected: late work for a job the pool no longer has
        for(var i = 0; i < 60; i++)
        {
            var response = await late.Response(await late.Send("mining.submit", "Rgood.rig1", "no-such-job", "0x0", "0x0", "0x0"));

            Assert.NotNull(response);
            Assert.Contains("invalid jobid", response!["error"]!.ToString());
        }

        await Task.Delay(200);
        Assert.Equal(0, rig.BanCalls);
        Assert.False(late.Closed);

        // the same count of shares that are wrong for the job the pool does have still bans
        var (bad, extraNonce) = await Miner(rig);
        var job = bad.Notifies("mining.notify").Last();
        var header = (string) job["params"]![1];

        // one at a time, as a miner does: 60 unread answers would overflow the connection's send queue (16) and drop the
        // socket without a ban, which is not what this test is about
        for(var i = 0; i < 60 && !bad.Closed; i++)
        {
            try
            {
                await bad.Response(await bad.Send("mining.submit", "Rgood.rig1", (string) job["params"]![0], "0x" + extraNonce + i.ToString("x12"), "0x" + header, "0x" + new string('0', 64)));
            }

            catch(IOException)
            {
                break;
            }
        }

        Assert.True(await Until(() => rig.BanCalls == 1));
        Assert.True(await Until(() => bad.Closed));
        Assert.False(late.Closed);
    }

    [Fact]
    public async Task A_closed_connection_gives_its_extranonce_back_through_the_real_teardown()
    {
        using var rig = NewRig(provider: new TinyProvider(1));
        var (first, extraNonce) = await Subscribe(rig);

        first.Dispose();

        string again = null;

        Assert.True(await Until(() =>
        {
            var (next, value) = Subscribe(rig).GetAwaiter().GetResult();
            again = value;
            return value != null;
        }));

        Assert.Equal(extraNonce, again);
    }

    [Fact]
    public async Task A_connection_that_does_not_authorize_in_time_is_closed()
    {
        using var rig = NewRig();
        rig.Pool.UnauthorizedLimit = TimeSpan.FromSeconds(2);

        var (subscribedOnly, _) = await Subscribe(rig);
        var (authorized, _) = await Miner(rig);

        Assert.True(await Until(() => subscribedOnly.Closed));

        await Task.Delay(1000);
        Assert.False(authorized.Closed);
    }

    [Fact]
    public async Task A_dead_daemon_sends_miners_nothing()
    {
        using var rig = NewRig();
        var (client, _) = await Miner(rig);
        var before = client.Notifies("mining.notify").Length;
        var emitted = 0;
        var attempts = 0;

        // a daemon that drops every request, so the test sees that forced refreshes really ran
        var daemon = new TcpListener(IPAddress.Loopback, 0);
        daemon.Start();
        var daemonPort = ((IPEndPoint) daemon.LocalEndpoint).Port;

        _ = Task.Run(async () =>
        {
            try
            {
                while(true)
                {
                    using var dropped = await daemon.AcceptTcpClientAsync(rig.Cts.Token);
                    Interlocked.Increment(ref attempts);
                }
            }

            catch(OperationCanceledException)
            {
                daemon.Stop();
            }
        });

        rig.Manager.StartWithDeadDaemon(new RpcClient(new DaemonEndpointConfig { Host = "127.0.0.1", Port = daemonPort }, jsonSerializerSettings, new MessageBus(), "rvn"), rig.Cts.Token);

        using var subscription = rig.Manager.Jobs.Subscribe(job =>
        {
            emitted++;
            rig.Pool.Broadcast(job).Wait();
        });

        await Task.Delay(3500);   // jobRebroadcastTimeout is 1 s here

        Assert.True(attempts >= 2, $"only {attempts} refresh attempts reached the daemon");
        Assert.Equal(0, emitted);
        Assert.Equal(before, client.Notifies("mining.notify").Length);
    }

    [Fact]
    public void A_forced_refresh_in_the_same_template_second_does_not_rebuild()
    {
        var template = JsonConvert.DeserializeObject<BlockTemplate>(Template, jsonSerializerSettings);
        var current = new ProgpowJobWithTemplate(template);

        Assert.True(ProgpowJobManager.IsSameHeaderRebuild(false, true, template, current));

        var later = JsonConvert.DeserializeObject<BlockTemplate>(Template, jsonSerializerSettings);
        later!.CurTime++;

        Assert.False(ProgpowJobManager.IsSameHeaderRebuild(false, true, later, current));   // the header changes
        Assert.False(ProgpowJobManager.IsSameHeaderRebuild(true, true, template, current));  // a new block always builds
        Assert.False(ProgpowJobManager.IsSameHeaderRebuild(false, false, template, current));
        Assert.False(ProgpowJobManager.IsSameHeaderRebuild(false, true, template, null));
    }

    private class ProgpowJobWithTemplate : ProgpowJob
    {
        public ProgpowJobWithTemplate(BlockTemplate template) => BlockTemplate = template;
    }
}
