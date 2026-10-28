using Common.Job;
using Common.Network;
using Game.Network;
using System.Text;

namespace Game.Room
{
    public class GameRoom : JobSerializer
    {
        private const float MapSize = 100f;
        private const int CellCount = 10;
        private const float CellSize = MapSize / CellCount;
        private const int TickIntervalMs = 50;
        private const int MaxNameBytes = 64;
        private const int MaxTextBytes = 1024;
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private readonly Dictionary<IGameSession, Player> _players =
            new Dictionary<IGameSession, Player>();
        private readonly Dictionary<(int X, int Y), HashSet<Player>> _cells =
            new Dictionary<(int X, int Y), HashSet<Player>>();

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
            Dictionary<Player, byte[]> movePackets = new Dictionary<Player, byte[]>();
            foreach (Player player in _players.Values)
            {
                if (!player.Moved || HasDisconnected(player.Session))
                {
                    continue;
                }

                (int X, int Y) oldCell = CellOf(player);
                player.X = player.PendingX;
                player.Y = player.PendingY;
                if (oldCell != CellOf(player))
                {
                    RemoveFromCell(player, oldCell);
                    AddToCell(player);
                }

                Console.WriteLine($"[GameRoom] 틱: {player.Session.Name} 위치 알림 → ({player.X}, {player.Y})");
                movePackets[player] = new MoveNtf { Name = player.Session.Name, X = player.X, Y = player.Y }.ToPacket();
            }

            // 모든 위치를 확정한 다음 시야를 비교해야 이동 요청 순서가 결과를 바꾸지 않는다.
            foreach (Player observer in _players.Values)
            {
                if (HasDisconnected(observer.Session))
                {
                    continue;
                }

                HashSet<Player> visible = FindNearbyPlayers(observer);
                foreach (Player previous in observer.KnownPlayers)
                {
                    if (!visible.Contains(previous))
                    {
                        observer.Session.SendPacket(new PlayerLeaveNtf { Name = previous.Session.Name }.ToPacket());
                    }
                }

                foreach (Player other in visible)
                {
                    if (!observer.KnownPlayers.Contains(other))
                    {
                        observer.Session.SendPacket(EnterPacket(other));
                    }
                    else if (movePackets.TryGetValue(other, out byte[]? packet))
                    {
                        observer.Session.SendPacket(packet);
                    }
                }

                if (movePackets.TryGetValue(observer, out byte[]? ownPacket))
                {
                    observer.Session.SendPacket(ownPacket);
                }

                observer.KnownPlayers.Clear();
                observer.KnownPlayers.UnionWith(visible);
            }

            foreach (Player player in _players.Values)
            {
                player.Moved = false;
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
            AddToCell(player);
            Console.WriteLine($"[GameRoom] 로그인: {name} (접속 {_players.Count}명)");
            session.SendPacket(new LoginRes { Message = $"환영합니다, {name}님" }.ToPacket());

            byte[] enterPacket = EnterPacket(player);
            foreach (Player other in FindNearbyPlayers(player))
            {
                player.KnownPlayers.Add(other);
                other.KnownPlayers.Add(player);
                session.SendPacket(EnterPacket(other));
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
            session.SendPacket(new EchoRes { Message = message }.ToPacket());
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

            Player player = _players[session];
            int recipientCount = 1 + player.KnownPlayers.Count(other => !HasDisconnected(other.Session));
            Console.WriteLine($"[GameRoom] 채팅: {session.Name} → '{message}' ({recipientCount}명에게)");
            byte[] packet = new ChatNtf { Message = $"{session.Name}: {message}" }.ToPacket();
            session.SendPacket(packet);
            foreach (Player other in player.KnownPlayers)
            {
                if (!HasDisconnected(other.Session))
                {
                    other.Session.SendPacket(packet);
                }
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

            player.PendingX = req.X;
            player.PendingY = req.Y;
            player.Moved = true;
            Console.WriteLine($"[GameRoom] 이동 기록: {session.Name} → ({req.X}, {req.Y})");
        }

        public void HandleLeave(IGameSession session)
        {
            session.State = GameSessionState.Disconnected;
            session.Disconnect();

            if (!_players.Remove(session, out Player? player))
            {
                return;
            }

            RemoveFromCell(player, CellOf(player));
            Console.WriteLine($"[GameRoom] 퇴장: {session.Name} (접속 {_players.Count}명)");
            byte[] packet = new PlayerLeaveNtf { Name = session.Name }.ToPacket();
            foreach (Player other in player.KnownPlayers)
            {
                if (other.KnownPlayers.Remove(player))
                {
                    other.Session.SendPacket(packet);
                }
            }
            player.KnownPlayers.Clear();
        }

        private static (int X, int Y) CellOf(Player player) =>
            (Math.Min((int)(player.X / CellSize), CellCount - 1),
             Math.Min((int)(player.Y / CellSize), CellCount - 1));

        private void AddToCell(Player player)
        {
            (int X, int Y) cell = CellOf(player);
            if (!_cells.TryGetValue(cell, out HashSet<Player>? players))
            {
                players = new HashSet<Player>();
                _cells[cell] = players;
            }

            players.Add(player);
        }

        private void RemoveFromCell(Player player, (int X, int Y) cell)
        {
            HashSet<Player> players = _cells[cell];
            players.Remove(player);
            if (players.Count == 0)
            {
                _cells.Remove(cell);
            }
        }

        private HashSet<Player> FindNearbyPlayers(Player observer)
        {
            HashSet<Player> visible = new HashSet<Player>();
            (int X, int Y) cell = CellOf(observer);
            for (int x = Math.Max(0, cell.X - 1); x <= Math.Min(CellCount - 1, cell.X + 1); x++)
            {
                for (int y = Math.Max(0, cell.Y - 1); y <= Math.Min(CellCount - 1, cell.Y + 1); y++)
                {
                    if (!_cells.TryGetValue((x, y), out HashSet<Player>? players))
                    {
                        continue;
                    }

                    foreach (Player player in players)
                    {
                        if (player != observer && !HasDisconnected(player.Session))
                        {
                            visible.Add(player);
                        }
                    }
                }
            }

            return visible;
        }

        private static byte[] EnterPacket(Player player) =>
            new PlayerEnterNtf { Name = player.Session.Name, X = player.X, Y = player.Y }.ToPacket();

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
