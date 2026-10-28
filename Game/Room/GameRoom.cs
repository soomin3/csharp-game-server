using Common.Job;
using Common.Network;
using Game.Network;
using System.Text;

namespace Game.Room
{
    public class GameRoom : JobSerializer
    {
        private const float MapSize = 100f;
        private const int TickIntervalMs = 50;
        private const int MaxNameBytes = 64;
        private const int MaxTextBytes = 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

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
                if (!player.Moved || HasDisconnected(player.Session))
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
            if (HasDisconnected(session))
            {
                return;
            }

            if (session.State != GameSessionState.Connected || _players.ContainsKey(session))
            {
                Reject(session, "중복 로그인");
                return;
            }

            if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || !IsValidText(name, MaxNameBytes))
            {
                Reject(session, "잘못된 이름 (UTF-8 1~64바이트, 앞뒤 공백과 제어문자 금지)");
                return;
            }

            foreach (Player other in _players.Values)
            {
                if (string.Equals(other.Session.Name, name, StringComparison.Ordinal))
                {
                    Reject(session, "이미 사용 중인 이름");
                    return;
                }
            }

            session.Name = name;
            session.State = GameSessionState.LoggedIn;
            Player player = new Player(session);
            _players[session] = player;
            Console.WriteLine($"[GameRoom] 로그인: {name} (접속 {_players.Count}명)");
            session.SendPacket(PacketBuilder.Build(PacketId.Login, $"환영합니다, {name}님"));

            byte[] enterPacket = new PlayerEnterNtf { Name = name, X = player.X, Y = player.Y }.ToPacket();
            foreach (Player other in _players.Values)
            {
                if (other.Session == session)
                {
                    continue;
                }

                session.SendPacket(new PlayerEnterNtf { Name = other.Session.Name, X = other.X, Y = other.Y }.ToPacket());
                other.Session.SendPacket(enterPacket);
            }
        }

        public void HandleEcho(IGameSession session, string message)
        {
            if (HasDisconnected(session))
            {
                return;
            }

            if (!IsValidText(message, MaxTextBytes))
            {
                Reject(session, "잘못된 에코 문자열");
                return;
            }

            Console.WriteLine($"[GameRoom] 에코: {session.Name} → '{message}'");
            session.SendPacket(PacketBuilder.Build(PacketId.Echo, message));
        }

        public void HandleChat(IGameSession session, string message)
        {
            if (!RequireLogin(session))
            {
                return;
            }

            if (!IsValidText(message, MaxTextBytes))
            {
                Reject(session, "잘못된 채팅 문자열");
                return;
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            Console.WriteLine($"[GameRoom] 채팅: {session.Name} → '{message}' ({_players.Count}명에게)");
            byte[] packet = PacketBuilder.Build(PacketId.Chat, $"{session.Name}: {message}");
            foreach (IGameSession other in _players.Keys)
            {
                other.SendPacket(packet);
            }
        }

        public void HandleMove(IGameSession session, MoveReq req)
        {
            if (!RequireLogin(session))
            {
                return;
            }

            if (!float.IsFinite(req.X) || !float.IsFinite(req.Y))
            {
                Reject(session, "유한한 숫자가 아닌 이동 좌표");
                return;
            }

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
            session.State = GameSessionState.Disconnected;
            session.Disconnect();

            if (!_players.Remove(session))
            {
                return;
            }

            Console.WriteLine($"[GameRoom] 퇴장: {session.Name} (접속 {_players.Count}명)");
            byte[] packet = new PlayerLeaveNtf { Name = session.Name }.ToPacket();
            foreach (IGameSession other in _players.Keys)
            {
                other.SendPacket(packet);
            }
        }

        private static bool IsInMap(float value) => value >= 0 && value <= MapSize;

        private static bool HasDisconnected(IGameSession session) =>
            session.IsDisconnected || session.State == GameSessionState.Disconnected;

        private bool RequireLogin(IGameSession session)
        {
            if (HasDisconnected(session))
            {
                return false;
            }

            if (session.State != GameSessionState.LoggedIn || !_players.ContainsKey(session))
            {
                Reject(session, "로그인 전 요청");
                return false;
            }

            return true;
        }

        private void Reject(IGameSession session, string reason)
        {
            Console.WriteLine($"[GameRoom] 요청 거부: {reason} — 연결을 끊습니다.");
            HandleLeave(session);
        }

        private static bool IsValidText(string text, int maxBytes)
        {
            if (text == null || text.Any(char.IsControl))
            {
                return false;
            }

            try
            {
                return StrictUtf8.GetByteCount(text) <= maxBytes;
            }
            catch (EncoderFallbackException)
            {
                return false;
            }
        }
    }
}
