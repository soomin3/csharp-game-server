using Common.Network;
using Game.Network;
using Game.Room;

GameRoom room = new GameRoom();
GameHandler handler = new GameHandler(room);
_ = room.RunAsync();

TServer server = new TServer(7777, client => new GameSession(client, handler));
await server.RunAsync();