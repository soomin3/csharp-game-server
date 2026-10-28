using Common.Network;
using Game.Room;
using Common.Auth;

namespace Game.Network
{
    public class GameHandler : IPacketHandler
    {
        private readonly GameRoom? _room;
        private readonly RoomManager? _manager;
        private readonly TokenService? _tokens;

        public GameHandler(GameRoom room, TokenService? tokens = null)
        {
            _room = room;
            _tokens = tokens;
        }

        public GameHandler(RoomManager manager, TokenService? tokens = null)
        {
            _manager = manager;
            _tokens = tokens;
        }

        public void ResLogin(IGameSession session, LoginReq packet)
        {
            if (session.IsDisconnected) { return; }
            if (_tokens == null || !_tokens.TryValidate(packet.Token, out TokenIdentity? identity))
            {
                session.Disconnect("로그인 토큰 검증 실패");
                OnDisconnect(session);
                return;
            }

            ResLogin(session, identity!.Name);
        }
        public void ResEcho(IGameSession session, EchoReq packet) => ResEcho(session, packet.Message);
        public void ResChat(IGameSession session, ChatReq packet) => ResChat(session, packet.Message);
        public void ResChangeRoom(IGameSession session, ChangeRoomReq packet) => ResChangeRoom(session, packet.RoomId);
        // 검증된 서버 내부 호출과 도메인 테스트용 진입점이다.
        // 네트워크 LoginReq는 위의 타입 있는 함수에서 토큰을 검증한 뒤에만 이 함수를 호출한다.
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
