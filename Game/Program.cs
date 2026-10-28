using Common.Network;
using Game.Network;
using Game.Room;
using Common.Job;
using Common.Auth;

int port = ReadOption("--port", 7777);
int workerCount = ReadOption("--workers", 2);
int roomCount = ReadOption("--rooms", 3);
TokenService tokens = TokenService.FromEnvironment();
await using RoomScheduler scheduler = new RoomScheduler(workerCount);
RoomManager manager = new RoomManager(scheduler, roomCount);
GameHandler handler = new GameHandler(manager, tokens);
foreach (GameRoom room in manager.Rooms)
{
    _ = room.RunTickLoopAsync();
}

TServer server = new TServer(port, client => new GameSession(client, handler));
await server.RunAsync();

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
