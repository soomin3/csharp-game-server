using Common.Network;
using Game.Room;

namespace Game.Network
{
    public class GameHandler : IPacketHandler
    {
        private readonly GameRoom? _room;
        private readonly RoomManager? _manager;

        public GameHandler(GameRoom room)
        {
            _room = room;
        }

        public GameHandler(RoomManager manager)
        {
            _manager = manager;
        }

        public void ResLogin(IGameSession session, LoginReq packet) => ResLogin(session, packet.Name);
        public void ResEcho(IGameSession session, EchoReq packet) => ResEcho(session, packet.Message);
        public void ResChat(IGameSession session, ChatReq packet) => ResChat(session, packet.Message);
        public void ResChangeRoom(IGameSession session, ChangeRoomReq packet) => ResChangeRoom(session, packet.RoomId);
        public void ResLogin(IGameSession session, string name)
        {
            if (_manager != null) { _manager.Login(session, name); }
            else { _room!.Push(() => _room.HandleLogin(session, name)); }
        }

        public void ResEcho(IGameSession session, string message)
        {
            Post(session, room => room.HandleEcho(session, message));
        }

        public void ResChat(IGameSession session, string message)
        {
            Post(session, room => room.HandleChat(session, message));
        }

        public void ResMove(IGameSession session, MoveReq req)
        {
            Post(session, room => room.HandleMove(session, req));
        }

        public void OnDisconnect(IGameSession session)
        {
            if (_manager != null) { _manager.Disconnect(session); }
            else { _room!.Push(() => _room.HandleLeave(session)); }
        }

        public void ResChangeRoom(IGameSession session, int roomId)
        {
            if (_manager != null) { _manager.ChangeRoom(session, roomId); }
            else { _room!.Push(() => _room.HandleChangeRoom(session, roomId)); }
        }

        private void Post(IGameSession session, Action<GameRoom> action)
        {
            if (_manager != null) { _manager.Post(session, action); }
            else { _room!.Push(() => action(_room)); }
        }
    }
}
