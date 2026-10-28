using Common.Job;
using Game.Network;

namespace Game.Room
{
    public sealed class RoomManager
    {
        private readonly object _gate = new object();
        private readonly Dictionary<IGameSession, Membership> _members = new Dictionary<IGameSession, Membership>();
        private readonly Dictionary<string, Membership> _names = new Dictionary<string, Membership>(StringComparer.Ordinal);

        public RoomManager(RoomScheduler scheduler, int roomCount = 3)
        {
            ArgumentNullException.ThrowIfNull(scheduler);
            if (roomCount <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(roomCount));
            }

            Rooms = Enumerable.Range(1, roomCount).Select(id => new GameRoom(id, scheduler)).ToArray();
        }

        public IReadOnlyList<GameRoom> Rooms { get; }
        public GameRoom GetRoom(int roomId) => Rooms[roomId - 1];
        public int ReservedNameCount { get { lock (_gate) { return _names.Count; } } }
        public int? GetCurrentRoomId(IGameSession session)
        {
            lock (_gate) { return _members.TryGetValue(session, out Membership? member) ? member.Room.RoomId : null; }
        }

        public bool IsTransferring(IGameSession session)
        {
            lock (_gate) { return _members.TryGetValue(session, out Membership? member) && member.Transferring; }
        }

        public void Login(IGameSession session, string name)
        {
            lock (_gate)
            {
                if (HasDisconnected(session)) { return; }
                if (_members.TryGetValue(session, out Membership? existing))
                {
                    if (!existing.Transferring && !existing.Closing)
                    {
                        PostLocked(existing, room => room.HandleLogin(session, name));
                    }
                    return;
                }

                if (!GameRoom.IsValidName(name))
                {
                    Rooms[0].Push(() => Rooms[0].HandleLogin(session, name));
                    return;
                }

                if (_names.ContainsKey(name))
                {
                    session.Disconnect("이미 사용 중인 이름");
                    Rooms[0].Push(() => Rooms[0].HandleLeave(session));
                    return;
                }

                Membership member = new Membership(session, name, Rooms[0]);
                _members.Add(session, member);
                _names.Add(name, member);
                PostLocked(member, room => room.HandleLogin(session, name));
            }
        }

        public void Post(IGameSession session, Action<GameRoom> action)
        {
            lock (_gate)
            {
                if (HasDisconnected(session)) { return; }
                if (!_members.TryGetValue(session, out Membership? member))
                {
                    Rooms[0].Push(() => action(Rooms[0]));
                }
                else if (!member.Transferring && !member.Closing)
                {
                    PostLocked(member, action);
                }
            }
        }

        private void PostLocked(Membership member, Action<GameRoom> action)
        {
            GameRoom room = member.Room;
            room.Push(() =>
            {
                if (!CanRun(member, room)) { return; }
                action(room);
                if (HasDisconnected(member.Session)) { Disconnect(member.Session); }
            });
        }

        public void ChangeRoom(IGameSession session, int roomId)
        {
            lock (_gate)
            {
                if (HasDisconnected(session)) { return; }
                if (!_members.TryGetValue(session, out Membership? member))
                {
                    session.Disconnect("로그인 전 방 이동");
                    Rooms[0].Push(() => Rooms[0].HandleLeave(session));
                    return;
                }

                if (member.Closing || member.Transferring || roomId < 1 || roomId > Rooms.Count)
                {
                    return;
                }

                if (member.Room.RoomId == roomId)
                {
                    PostLocked(member, room => room.NotifyCurrentRoom(session));
                    return;
                }

                member.Transferring = true;
                long version = ++member.Version;
                GameRoom source = member.Room;
                GameRoom destination = GetRoom(roomId);
                source.Push(() => BeginTransfer(member, source, destination, version));
            }
        }

        private void BeginTransfer(Membership member, GameRoom source, GameRoom destination, long version)
        {
            if (!CanRun(member, source, version)) { return; }
            if (HasDisconnected(member.Session) || member.Session.State != GameSessionState.LoggedIn)
            {
                source.HandleLeave(member.Session);
                Release(member);
                return;
            }

            Player? player = source.DetachForTransfer(member.Session);
            if (player == null)
            {
                source.HandleLeave(member.Session);
                Release(member);
                return;
            }

            destination.Push(() => CompleteTransfer(member, destination, player, version));
        }

        private void CompleteTransfer(Membership member, GameRoom destination, Player player, long version)
        {
            bool disconnected;
            lock (_gate)
            {
                if (!IsCurrent(member) || member.Closing || member.Version != version)
                {
                    return;
                }

                disconnected = HasDisconnected(member.Session);
                if (!disconnected)
                {
                    member.Room = destination;
                    // 응답 직후 들어오는 요청은 현재 attach 일감 뒤의 목적지 줄에 들어간다.
                    member.Transferring = false;
                }
            }

            if (disconnected)
            {
                destination.HandleLeave(member.Session);
                Release(member);
                return;
            }

            if (!destination.AttachTransferred(player))
            {
                destination.HandleLeave(member.Session);
                Release(member);
                return;
            }

            bool close;
            lock (_gate)
            {
                close = !IsCurrent(member) || member.Closing || HasDisconnected(member.Session);
            }

            if (close)
            {
                destination.HandleLeave(member.Session);
                Release(member);
            }
        }

        public void Disconnect(IGameSession session)
        {
            session.Disconnect();
            lock (_gate)
            {
                if (!_members.TryGetValue(session, out Membership? member))
                {
                    Rooms[0].Push(() => Rooms[0].HandleLeave(session));
                    return;
                }

                if (member.Closing) { return; }
                member.Closing = true;
                GameRoom room = member.Room;
                room.Push(() => { room.HandleLeave(session); Release(member); });
            }
        }

        public void DisconnectAll()
        {
            IGameSession[] sessions;
            lock (_gate) { sessions = _members.Keys.ToArray(); }
            foreach (IGameSession session in sessions) { Disconnect(session); }
        }

        private bool CanRun(Membership member, GameRoom room, long? version = null)
        {
            lock (_gate)
            {
                return IsCurrent(member) && !member.Closing && member.Room == room &&
                    (!version.HasValue || member.Version == version.Value);
            }
        }

        private bool IsCurrent(Membership member) =>
            _members.TryGetValue(member.Session, out Membership? current) && current == member;

        private void Release(Membership member)
        {
            lock (_gate)
            {
                if (!IsCurrent(member)) { return; }
                _members.Remove(member.Session);
                if (_names.TryGetValue(member.Name, out Membership? owner) && owner == member)
                {
                    _names.Remove(member.Name);
                }
            }
        }

        private static bool HasDisconnected(IGameSession session) =>
            session.IsDisconnected || session.State == GameSessionState.Disconnected;

        private sealed class Membership
        {
            public Membership(IGameSession session, string name, GameRoom room)
            {
                Session = session;
                Name = name;
                Room = room;
            }

            public IGameSession Session { get; }
            public string Name { get; }
            public GameRoom Room { get; set; }
            public bool Transferring { get; set; }
            public bool Closing { get; set; }
            public long Version { get; set; }
        }
    }
}
