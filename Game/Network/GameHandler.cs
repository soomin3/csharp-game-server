using Common.Network;
using Game.Room;

namespace Game.Network
{
    public class GameHandler : IPacketHandler
    {
        private readonly GameRoom _room;

        public GameHandler(GameRoom room)
        {
            _room = room;
        }

        public void ResLogin(IGameSession session, LoginReq packet) => ResLogin(session, packet.Name);
        public void ResEcho(IGameSession session, EchoReq packet) => ResEcho(session, packet.Message);
        public void ResChat(IGameSession session, ChatReq packet) => ResChat(session, packet.Message);

        public void ResLogin(IGameSession session, string name)
        {
            _room.Push(() => _room.HandleLogin(session, name));
        }

        public void ResEcho(IGameSession session, string message)
        {
            _room.Push(() => _room.HandleEcho(session, message));
        }

        public void ResChat(IGameSession session, string message)
        {
            _room.Push(() => _room.HandleChat(session, message));
        }

        public void ResMove(IGameSession session, MoveReq req)
        {
            _room.Push(() => _room.HandleMove(session, req));
        }

        public void OnDisconnect(IGameSession session)
        {
            _room.Push(() => _room.HandleLeave(session));
        }
    }
}
