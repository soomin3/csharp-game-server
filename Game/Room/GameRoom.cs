using Common.Job;
using Common.Network;
using Game.Network;

namespace Game.Room
{
    public class GameRoom : JobSerializer
    {
        private const float MapSize = 100f;

        private readonly List<IGameSession> _sessions = new List<IGameSession>();

        public int SessionCount => _sessions.Count;

        public void HandleLogin(IGameSession session, string name)
        {
            session.Name = name;
            _sessions.Add(session);
            Console.WriteLine($"[GameRoom] 로그인: {name} (접속 {_sessions.Count}명)");
            session.SendPacket(PacketBuilder.Build(PacketId.Login, $"환영합니다, {name}님"));
        }

        public void HandleEcho(IGameSession session, string message)
        {
            Console.WriteLine($"[GameRoom] 에코: {session.Name} → '{message}'");
            session.SendPacket(PacketBuilder.Build(PacketId.Echo, message));
        }

        public void HandleChat(IGameSession session, string message)
        {
            Console.WriteLine($"[GameRoom] 채팅: {session.Name} → '{message}' ({_sessions.Count}명에게)");
            byte[] packet = PacketBuilder.Build(PacketId.Chat, $"{session.Name}: {message}");
            foreach (IGameSession other in _sessions)
            {
                other.SendPacket(packet);
            }
        }

        public void HandleMove(IGameSession session, MoveReq req)
        {
            if (!IsInMap(req.X) || !IsInMap(req.Y))
            {
                Console.WriteLine($"[GameRoom] 이동 거부: {session.Name} → ({req.X}, {req.Y}) 맵 밖");
                return;
            }

            Console.WriteLine($"[GameRoom] 이동: {session.Name} → ({req.X}, {req.Y})");
            byte[] packet = new MoveNtf { Name = session.Name, X = req.X, Y = req.Y }.ToPacket();
            foreach (IGameSession other in _sessions)
            {
                other.SendPacket(packet);
            }
        }

        public void HandleLeave(IGameSession session)
        {
            _sessions.Remove(session);
            Console.WriteLine($"[GameRoom] 퇴장: {session.Name} (접속 {_sessions.Count}명)");
        }

        private static bool IsInMap(float value) => value >= 0 && value <= MapSize;
    }
}