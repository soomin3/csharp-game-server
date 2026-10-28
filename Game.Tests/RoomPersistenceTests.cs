using System.Collections.Concurrent;
using Common.Job;
using Common.Network;
using Game.Network;
using Game.Persistence;
using Game.Room;

namespace Game.Tests;

public sealed class RoomPersistenceTests
{
    [Fact]
    public async Task 새_서버와_저장소에서도_확정_위치와_방을_복구한다()
    {
        string directory = Path.Combine(Path.GetTempPath(), "MyServer-db-integration-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "players.db");
        try
        {
            await using (World first = new World(new SqlitePlayerStore(path)))
            {
                FakeGameSession kim = new FakeGameSession();
                await first.LoginAsync(kim, "Kim");
                first.Handler.ResMove(kim, new MoveReq { X = 3.5f, Y = 7f });
                first.Manager.GetRoom(1).Push(first.Manager.GetRoom(1).Tick);
                await first.SettleAsync();
                first.Handler.ResChangeRoom(kim, 2); await first.SettleAsync();
                first.Handler.ResMove(kim, new MoveReq { X = 99, Y = 99 });
                first.Handler.OnDisconnect(kim); await first.SettleAsync();
            }
            await using World second = new World(new SqlitePlayerStore(path));
            FakeGameSession reconnected = new FakeGameSession();
            await second.LoginAsync(reconnected, "Kim");
            ChangeRoomNtf restored = RoomResponse(reconnected);
            Assert.Equal(2, restored.RoomId); Assert.Equal(3.5f, restored.X); Assert.Equal(7f, restored.Y);
            Assert.Equal(2, second.Manager.GetCurrentRoomId(reconnected));
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) { File.Delete(file); }
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task 로딩_중_대기한_요청과_입장_응답_직후_요청은_순서대로_처리한다()
    {
        StubStore store = new StubStore();
        TaskCompletionSource<PlayerRecord?> load = store.NewLoadGate();
        store.Load = name => name == "Kim" ? load.Task : Task.FromResult<PlayerRecord?>(null);
        await using World world = new World(store);
        FakeGameSession park = new FakeGameSession();
        await world.LoginAsync(park, "Park"); park.SentPackets.Clear();
        RoomResponseSession kim = new RoomResponseSession();
        kim.OnRoomResponse = _ => world.Handler.ResChat(kim, "third");
        world.Handler.ResLogin(kim, "Kim");
        world.Handler.ResChat(kim, "first"); world.Handler.ResChat(kim, "second");
        Assert.Empty(kim.SentPackets);
        load.SetResult(null); await world.SettleAsync();
        string[] expected = { "Kim: first", "Kim: second", "Kim: third" };
        Assert.Equal(expected, ChatMessages(kim));
        Assert.Equal(expected, ChatMessages(park));
        Assert.Equal(PacketId.Login, IdOf(kim.SentPackets[0]));
        Assert.False(kim.IsDisconnected);
    }

    [Fact]
    public async Task 대기_32개가_있어도_입장_ACK_직후_첫_채팅은_33번째로_정상_처리한다()
    {
        StubStore store = new StubStore();
        TaskCompletionSource<PlayerRecord?> load = store.NewLoadGate();
        store.Load = name => name == "Kim" ? load.Task : Task.FromResult<PlayerRecord?>(null);
        await using World world = new World(store);
        FakeGameSession park = new FakeGameSession(); await world.LoginAsync(park, "Park");
        park.SentPackets.Clear();
        RoomResponseSession kim = new RoomResponseSession();
        kim.OnRoomResponse = _ => world.Handler.ResChat(kim, "after-ACK");
        world.Handler.ResLogin(kim, "Kim");
        for (int i = 0; i < 32; i++) { world.Handler.ResChat(kim, $"queued-{i}"); }

        load.SetResult(null); await world.SettleAsync();

        Assert.False(kim.IsDisconnected);
        string[] expected = Enumerable.Range(0, 32).Select(i => $"Kim: queued-{i}").Append("Kim: after-ACK").ToArray();
        Assert.Equal(expected, ChatMessages(kim));
        Assert.Equal(expected, ChatMessages(park));
        Assert.Equal(GameSessionState.LoggedIn, kim.State);
        Assert.Equal(2, world.Manager.ReservedNameCount);
    }

    [Fact]
    public async Task 초기_방_알림_직후_요청한_이동의_busy를_유지하고_후속_채팅은_무시한다()
    {
        StubStore store = new StubStore(); await using World world = new World(store);
        RoomResponseSession kim = new RoomResponseSession();
        bool? transferWasBusy = null;
        kim.OnRoomResponse = notification =>
        {
            if (notification.RoomId == 1)
            {
                world.Manager.GetRoom(1).Push(() => transferWasBusy = world.Manager.IsTransferring(kim));
                world.Handler.ResChangeRoom(kim, 2);
                world.Handler.ResChat(kim, "ignored during transfer");
            }
        };

        await world.LoginAsync(kim, "Kim");

        Assert.True(transferWasBusy);
        Assert.False(kim.IsDisconnected);
        Assert.Equal(2, world.Manager.GetCurrentRoomId(kim));
        Assert.False(world.Manager.IsTransferring(kim));
        Assert.Empty(ChatMessages(kim));
    }

    [Fact]
    public async Task 로딩_중_연결을_끊으면_늦은_결과를_버리고_기본_위치를_저장하지_않는다()
    {
        StubStore store = new StubStore();
        TaskCompletionSource<PlayerRecord?> load = store.NewLoadGate(); store.Load = _ => load.Task;
        await using World world = new World(store);
        FakeGameSession kim = new FakeGameSession();
        world.Handler.ResLogin(kim, "Kim"); world.Handler.ResChat(kim, "discard");
        world.Handler.OnDisconnect(kim); await world.Scheduler.DrainAsync();
        load.SetResult(new PlayerRecord("Kim", 80, 80, 2)); await world.SettleAsync();
        Assert.All(world.Manager.Rooms, room => Assert.Equal(0, room.SessionCount));
        Assert.Equal(0, world.Manager.ReservedNameCount);
        Assert.Empty(kim.SentPackets); Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task 같은_세션의_로딩_중_재로그인은_연결을_끊는다()
    {
        StubStore store = new StubStore();
        TaskCompletionSource<PlayerRecord?> load = store.NewLoadGate(); store.Load = _ => load.Task;
        await using World world = new World(store);
        FakeGameSession kim = new FakeGameSession();
        world.Handler.ResLogin(kim, "Kim"); world.Handler.ResLogin(kim, "Other");
        load.SetResult(null); await world.SettleAsync();
        Assert.True(kim.IsDisconnected); Assert.Null(world.Manager.GetCurrentRoomId(kim));
        Assert.Equal(0, world.Manager.ReservedNameCount); Assert.Empty(store.Saves);
    }

    [Theory]
    [InlineData(32, false)]
    [InlineData(33, true)]
    public async Task 로딩_중_대기는_32개까지만_허용한다(int count, bool closed)
    {
        StubStore store = new StubStore();
        TaskCompletionSource<PlayerRecord?> load = store.NewLoadGate(); store.Load = _ => load.Task;
        await using World world = new World(store);
        FakeGameSession kim = new FakeGameSession();
        world.Handler.ResLogin(kim, "Kim");
        for (int i = 0; i < count; i++) { world.Handler.ResChat(kim, $"message-{i}"); }
        Assert.Equal(closed, kim.IsDisconnected);
        load.SetResult(null); await world.SettleAsync();
        Assert.Equal(closed ? 0 : 32, ChatMessages(kim).Count());
    }

    [Fact]
    public async Task 빠른_재접속의_Load보다_이전_퇴장의_Save를_먼저_enqueue한다()
    {
        StubStore store = new StubStore();
        TaskCompletionSource save = store.NewSaveGate();
        PlayerRecord? saved = null;
        int reads = 0;
        async Task<PlayerRecord?> ReadAfterSave() { await save.Task; return saved; }
        store.Load = _ => Interlocked.Increment(ref reads) == 1 ? Task.FromResult<PlayerRecord?>(null) : ReadAfterSave();
        store.Save = record => { saved = record; return save.Task; };
        await using World world = new World(store);
        FakeGameSession old = new FakeGameSession(); await world.LoginAsync(old, "Kim");
        world.Handler.ResMove(old, new MoveReq { X = 12, Y = 13 });
        world.Manager.GetRoom(1).Push(world.Manager.GetRoom(1).Tick); await world.Scheduler.DrainAsync();
        world.Handler.OnDisconnect(old); await world.Scheduler.DrainAsync();
        Assert.Equal(0, world.Manager.ReservedNameCount);
        Assert.Equal(1, world.Manager.PendingPersistenceCount);
        FakeGameSession next = new FakeGameSession(); world.Handler.ResLogin(next, "Kim");
        Assert.Equal(new[] { "load:Kim", "save:Kim", "load:Kim" }, store.Calls);
        save.SetResult(); await world.SettleAsync();
        Assert.Equal(12f, RoomResponse(next).X); Assert.Equal(13f, RoomResponse(next).Y);
    }

    [Fact]
    public async Task Reject로_먼저_제거된_플레이어의_확정_위치도_저장한다()
    {
        StubStore store = new StubStore(); await using World world = new World(store);
        FakeGameSession kim = new FakeGameSession(); await world.LoginAsync(kim, "Kim");
        world.Handler.ResMove(kim, new MoveReq { X = 5, Y = 6 });
        world.Manager.GetRoom(1).Push(world.Manager.GetRoom(1).Tick); await world.SettleAsync();
        world.Handler.ResChat(kim, "\u001b"); await world.SettleAsync();
        Assert.True(kim.IsDisconnected);
        Assert.Equal(new PlayerRecord("Kim", 5, 6, 1), Assert.Single(store.Saves));
        Assert.Equal(0, world.Manager.ReservedNameCount);
    }

    [Fact]
    public async Task detach와_attach_사이의_종료도_목적지_방과_확정_위치를_한_번_저장한다()
    {
        StubStore store = new StubStore(); await using World world = new World(store);
        FakeGameSession kim = new FakeGameSession(); await world.LoginAsync(kim, "Kim");
        world.Handler.ResMove(kim, new MoveReq { X = 3.5f, Y = 7 });
        world.Manager.GetRoom(1).Push(world.Manager.GetRoom(1).Tick); await world.SettleAsync();
        using ManualResetEventSlim started = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        world.Manager.GetRoom(2).Push(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        try
        {
            world.Handler.ResChangeRoom(kim, 2);
            await world.Manager.GetRoom(1).BarrierAsync().WaitAsync(TimeSpan.FromSeconds(5));
            world.Handler.OnDisconnect(kim);
            await world.Manager.GetRoom(1).BarrierAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); }
        await world.SettleAsync();
        Assert.Equal(new PlayerRecord("Kim", 3.5f, 7, 2), Assert.Single(store.Saves));
        Assert.All(world.Manager.Rooms, room => Assert.Equal(0, room.SessionCount));
        Assert.Equal(0, world.Manager.ReservedNameCount);
    }

    [Fact]
    public async Task 로드_실패는_연결을_닫고_Drain에서_관측한다()
    {
        StubStore store = new StubStore { Load = _ => Task.FromException<PlayerRecord?>(new IOException("load failed")) };
        await using World world = new World(store);
        FakeGameSession kim = new FakeGameSession(); world.Handler.ResLogin(kim, "Kim");
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => world.Manager.DrainPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        await world.Scheduler.DrainAsync();
        Assert.Contains(error.InnerExceptions, failure => failure.Message == "load failed");
        Assert.True(kim.IsDisconnected); Assert.Empty(store.Saves);
        Assert.Equal(0, world.Manager.PendingPersistenceCount); Assert.Equal(0, world.Manager.ReservedNameCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task 저장의_동기_예외와_비동기_실패를_모두_관측한다(bool synchronous)
    {
        StubStore store = new StubStore();
        store.Save = _ => synchronous ? throw new IOException("save failed") : Task.FromException(new IOException("save failed"));
        await using World world = new World(store);
        FakeGameSession kim = new FakeGameSession(); await world.LoginAsync(kim, "Kim");
        world.Handler.OnDisconnect(kim); await world.Scheduler.DrainAsync();
        AggregateException error = await Assert.ThrowsAsync<AggregateException>(() => world.Manager.DrainPersistenceAsync());
        Assert.Contains(error.InnerExceptions, failure => failure.Message == "save failed");
        Assert.Equal(0, world.Manager.PendingPersistenceCount); Assert.Equal(0, world.Manager.ReservedNameCount);
        Assert.Single(store.Saves);
    }

    [Fact]
    public async Task 같은_이름으로_다시_로그인해도_이전_로드_결과는_새_세대를_덮지_않는다()
    {
        StubStore store = new StubStore();
        TaskCompletionSource<PlayerRecord?> first = store.NewLoadGate();
        TaskCompletionSource<PlayerRecord?> next = store.NewLoadGate();
        int reads = 0; store.Load = _ => Interlocked.Increment(ref reads) == 1 ? first.Task : next.Task;
        await using World world = new World(store);
        FakeGameSession old = new FakeGameSession(); world.Handler.ResLogin(old, "Kim");
        world.Handler.OnDisconnect(old); await world.Scheduler.DrainAsync();
        FakeGameSession current = new FakeGameSession(); world.Handler.ResLogin(current, "Kim");
        next.SetResult(new PlayerRecord("Kim", 3.5f, 7, 2));
        await world.Manager.GetRoom(2).BarrierAsync();
        first.SetResult(new PlayerRecord("Kim", 80, 80, 3)); await world.SettleAsync();
        Assert.Equal(2, world.Manager.GetCurrentRoomId(current));
        Assert.Equal(3.5f, RoomResponse(current).X); Assert.Empty(old.SentPackets);
        Assert.Equal(1, world.Manager.ReservedNameCount); Assert.Empty(store.Saves);
    }

    [Fact]
    public async Task 저장된_먼_위치를_처음부터_적용하여_원점에_거짓_입장을_알리지_않는다()
    {
        StubStore store = new StubStore { Load = name => Task.FromResult<PlayerRecord?>(name == "Kim" ? new PlayerRecord(name, 80, 80, 1) : null) };
        await using World world = new World(store);
        FakeGameSession park = new FakeGameSession(); await world.LoginAsync(park, "Park"); park.SentPackets.Clear();
        FakeGameSession kim = new FakeGameSession(); await world.LoginAsync(kim, "Kim");
        Assert.Empty(park.SentPackets);
        Assert.Equal(80f, RoomResponse(kim).X);
        Assert.DoesNotContain(kim.SentPackets, packet => IdOf(packet) == PacketId.PlayerEnterNtf);
    }

    [Fact]
    public async Task 로딩_중_방_이동과_그_뒤_채팅도_기존_이동_정책으로_재생한다()
    {
        StubStore store = new StubStore(); TaskCompletionSource<PlayerRecord?> load = store.NewLoadGate(); store.Load = _ => load.Task;
        await using World world = new World(store);
        FakeGameSession kim = new FakeGameSession(); world.Handler.ResLogin(kim, "Kim");
        world.Handler.ResChangeRoom(kim, 2); world.Handler.ResChat(kim, "ignored during transfer");
        load.SetResult(null); await world.SettleAsync();
        Assert.Equal(2, world.Manager.GetCurrentRoomId(kim)); Assert.False(kim.IsDisconnected);
        Assert.Empty(ChatMessages(kim));
    }

    [Fact]
    public async Task persistence_Drain은_SQL완료_뒤에도_결과_actor가_처리될_때까지_기다린다()
    {
        StubStore store = new StubStore();
        TaskCompletionSource<PlayerRecord?> load = store.NewLoadGate(); store.Load = _ => load.Task;
        await using World world = new World(store);
        using ManualResetEventSlim started = new ManualResetEventSlim();
        using ManualResetEventSlim release = new ManualResetEventSlim();
        world.Manager.GetRoom(1).Push(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
        Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
        FakeGameSession kim = new FakeGameSession(); world.Handler.ResLogin(kim, "Kim");
        Task drain = world.Manager.DrainPersistenceAsync();
        try
        {
            load.SetResult(null);
            Task delay = Task.Delay(100);
            Assert.Same(delay, await Task.WhenAny(drain, delay));
            Assert.Equal(1, world.Manager.PendingPersistenceCount);
            Assert.Empty(kim.SentPackets);
        }
        finally { release.Set(); }
        await drain.WaitAsync(TimeSpan.FromSeconds(5));
        await world.Scheduler.DrainAsync();
        Assert.Equal(GameSessionState.LoggedIn, kim.State);
        Assert.Equal(0, world.Manager.PendingPersistenceCount);
    }

    private static PacketId IdOf(byte[] packet) => (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize);
    private static ChangeRoomNtf RoomResponse(FakeGameSession session)
    {
        byte[] packet = session.SentPackets.Single(value => IdOf(value) == PacketId.ChangeRoomNtf);
        return ChangeRoomNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size)!;
    }
    private static IEnumerable<string> ChatMessages(FakeGameSession session) => session.SentPackets.Where(packet => IdOf(packet) == PacketId.Chat)
        .Select(packet => ChatNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size)!.Message);

    private sealed class RoomResponseSession : FakeGameSession, IGameSession
    {
        public Action<ChangeRoomNtf>? OnRoomResponse { get; set; }
        void IGameSession.SendPacket(byte[] packet)
        {
            base.SendPacket(packet);
            if (!IsDisconnected && IdOf(packet) == PacketId.ChangeRoomNtf)
            {
                OnRoomResponse?.Invoke(ChangeRoomNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size)!);
            }
        }
    }
    private sealed class StubStore : IPlayerStore
    {
        public Func<string, Task<PlayerRecord?>> Load { get; set; } = _ => Task.FromResult<PlayerRecord?>(null);
        public Func<PlayerRecord, Task> Save { get; set; } = _ => Task.CompletedTask;
        public ConcurrentQueue<string> Calls { get; } = new ConcurrentQueue<string>();
        public ConcurrentQueue<PlayerRecord> Saves { get; } = new ConcurrentQueue<PlayerRecord>();
        private readonly List<Action> _release = new List<Action>();
        public TaskCompletionSource<PlayerRecord?> NewLoadGate()
        {
            TaskCompletionSource<PlayerRecord?> source = new TaskCompletionSource<PlayerRecord?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _release.Add(() => source.TrySetResult(null)); return source;
        }
        public TaskCompletionSource NewSaveGate()
        {
            TaskCompletionSource source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _release.Add(() => source.TrySetResult()); return source;
        }
        public void ReleasePending() { foreach (Action release in _release) { release(); } }
        public Task<PlayerRecord?> LoadAsync(string name) { Calls.Enqueue("load:" + name); return Load(name); }
        public Task SaveAsync(PlayerRecord record) { Calls.Enqueue("save:" + record.Name); Saves.Enqueue(record); return Save(record); }
        public Task DrainAsync() => Task.CompletedTask;
        public ValueTask DisposeAsync() { ReleasePending(); return ValueTask.CompletedTask; }
    }
    private sealed class World : IAsyncDisposable
    {
        private readonly IPlayerStore _store;
        public RoomScheduler Scheduler { get; } = new RoomScheduler(2);
        public RoomManager Manager { get; }
        public GameHandler Handler { get; }
        public World(IPlayerStore store) { _store = store; Manager = new RoomManager(Scheduler, playerStore: store); Handler = new GameHandler(Manager); }
        public async Task LoginAsync(FakeGameSession session, string name) { Handler.ResLogin(session, name); await SettleAsync(); }
        public async Task SettleAsync()
        {
            await Scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Manager.DrainPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Manager.DrainPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
        public async ValueTask DisposeAsync()
        {
            if (_store is StubStore stub) { stub.ReleasePending(); }
            Manager.DisconnectAll(); await Scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            try { await Manager.DrainPersistenceAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
            await Scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await Scheduler.DisposeAsync(); await _store.DisposeAsync();
        }
    }
}
