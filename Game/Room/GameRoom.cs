using Common.Job;
using Common.Network;
using Game.Network;

namespace Game.Room
{
    public class GameRoom : JobSerializer
    {
        private const float MapSize = 100f;
        private const int TickIntervalMs = 50;

        private readonly Dictionary<IGameSession, Player> _players =
            new Dictionary<IGameSession, Player>();

        public int SessionCount => _players.Count;

        public async Task RunTickLoopAsync()
        {
            using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(TickIntervalMs));
            while (await timer.WaitForNextTickAsync())
            {
                Push(Tick);
            }
        }

        public void Tick()
        {
            foreach (Player player in _players.Values)
            {
                if (!player.Moved)
                {
                    continue;
                }

                player.Moved = false;
                Console.WriteLine($"[GameRoom] 틱: {player.Session.Name} 위치 알림 → ({player.X}, {player.Y})");
                byte[] packet = new MoveNtf { Name = player.Session.Name, X = player.X, Y = player.Y }.ToPacket();
                foreach (IGameSession other in _players.Keys)
                {
                    other.SendPacket(packet);
                }
            }
        }

        public void HandleLogin(IGameSession session, string name)
        {
            session.Name = name;
            _players[session] = new Player(session);
            Console.WriteLine($"[GameRoom] 로그인: {name} (접속 {_players.Count}명)");
            session.SendPacket(PacketBuilder.Build(PacketId.Login, $"환영합니다, {name}님"));
        }

        public void HandleEcho(IGameSession session, string message)
        {
            Console.WriteLine($"[GameRoom] 에코: {session.Name} → '{message}'");
            session.SendPacket(PacketBuilder.Build(PacketId.Echo, message));
        }

        public void HandleChat(IGameSession session, string message)
        {
            Console.WriteLine($"[GameRoom] 채팅: {session.Name} → '{message}' ({_players.Count}명에게)");
            byte[] packet = PacketBuilder.Build(PacketId.Chat, $"{session.Name}: {message}");
            foreach (IGameSession other in _players.Keys)
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

            if (!_players.TryGetValue(session, out Player? player))
            {
                return;
            }

            player.X = req.X;
            player.Y = req.Y;
            player.Moved = true;
            Console.WriteLine($"[GameRoom] 이동 기록: {session.Name} → ({req.X}, {req.Y})");
        }

        public void HandleLeave(IGameSession session)
        {
            _players.Remove(session);
            Console.WriteLine($"[GameRoom] 퇴장: {session.Name} (접속 {_players.Count}명)");
        }

        private static bool IsInMap(float value) => value >= 0 && value <= MapSize;
    }
}
