using Common.Network;
using Game.Network;
using Game.Room;
using Common.Job;
using Common.Auth;
using Game.Persistence;

int port = ReadOption("--port", 7777);
int workerCount = ReadOption("--workers", 2);
int roomCount = ReadOption("--rooms", 3);
TokenService tokens = TokenService.FromEnvironment();
string dataPath = ReadStringOption("--data", Path.Combine(AppContext.BaseDirectory, "data", "players.db"));
await using IPlayerStore store = new SqlitePlayerStore(dataPath);
await store.DrainAsync();
await using RoomScheduler scheduler = new RoomScheduler(workerCount);
RoomManager manager = new RoomManager(scheduler, roomCount, store);
GameHandler handler = new GameHandler(manager, tokens);
using CancellationTokenSource tickStop = new CancellationTokenSource();
List<Task> timers = new List<Task>();
foreach (GameRoom room in manager.Rooms)
{
    timers.Add(room.RunTickLoopAsync(tickStop.Token));
}

TServer server = new TServer(port, client => new GameSession(client, handler));
try { await server.RunAsync(); }
finally
{
    tickStop.Cancel();
    await Task.WhenAll(timers);
    manager.DisconnectAll();
    await scheduler.DrainAsync();
    await manager.DrainPersistenceAsync();
    await store.DrainAsync();
    await scheduler.DrainAsync();
}

string ReadStringOption(string name, string fallback)
{
    int index = Array.IndexOf(args, name);
    if (index < 0) { return fallback; }
    if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
    {
        throw new ArgumentException($"{name} 뒤에 경로가 필요합니다.");
    }
    return args[index + 1];
}

int ReadOption(string name, int fallback)
{
    int index = Array.IndexOf(args, name);
    if (index < 0) { return fallback; }
    if (index + 1 >= args.Length || !int.TryParse(args[index + 1], out int value) || value <= 0)
    {
        throw new ArgumentException($"{name} 뒤에 양의 정수가 필요합니다.");
    }
    return value;
}
