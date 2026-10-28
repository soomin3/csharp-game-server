using Common.Job;
using Game.Network;
using Game.Persistence;

namespace Game.Room
{
    public sealed class RoomManager
    {
        private const int MaxLoadingRequests = 32;
        private readonly object _gate = new object();
        private readonly Dictionary<IGameSession, Membership> _members = new Dictionary<IGameSession, Membership>();
        private readonly Dictionary<string, Membership> _names = new Dictionary<string, Membership>(StringComparer.Ordinal);
        private readonly IPlayerStore? _store;
        private readonly List<Exception> _persistenceErrors = new List<Exception>();
        private TaskCompletionSource _persistenceIdle = CompletedSource();
        private int _pendingPersistence;

        public RoomManager(RoomScheduler scheduler, int roomCount = 3, IPlayerStore? playerStore = null)
        {
            ArgumentNullException.ThrowIfNull(scheduler);
            if (roomCount <= 0) { throw new ArgumentOutOfRangeException(nameof(roomCount)); }
            _store = playerStore;
            Rooms = Enumerable.Range(1, roomCount).Select(id => new GameRoom(id, scheduler)).ToArray();
            foreach (GameRoom room in Rooms) { room.PlayerRemoved = RememberSnapshot; }
        }

        public IReadOnlyList<GameRoom> Rooms { get; }
        public GameRoom GetRoom(int roomId) => Rooms[roomId - 1];
        public int ReservedNameCount { get { lock (_gate) { return _names.Count; } } }
        public int PendingPersistenceCount { get { lock (_gate) { return _pendingPersistence; } } }
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
                    if (existing.Loading) { Disconnect(session); }
                    else if (!existing.Transferring && !existing.Closing)
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

                Membership member = new Membership(session, name, Rooms[0]) { Loading = _store != null };
                _members.Add(session, member);
                _names.Add(name, member);
                if (member.Loading) { StartLoadLocked(member); }
                else { PostLocked(member, room => room.HandleLogin(session, name)); }
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
                else if (!member.Closing && !DeferLocked(member, () => Post(session, action)) && !member.Transferring)
                {
                    PostLocked(member, action);
                }
            }
        }

        private bool DeferLocked(Membership member, Action replay)
        {
            if (!member.Loading) { return false; }
            if (member.Pending.Count == MaxLoadingRequests)
            {
                member.Session.Disconnect("로그인 중 대기 요청 한도 초과");
                Disconnect(member.Session);
            }
            else { member.Pending.Enqueue(replay); }
            return true;
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

        private void StartLoadLocked(Membership member)
        {
            BeginPersistenceLocked();
            Task<PlayerRecord?> operation;
            try { operation = _store!.LoadAsync(member.Name); }
            catch (Exception error) { operation = Task.FromException<PlayerRecord?>(error); }
            _ = ObserveLoadAsync(member, member.Version, operation);
        }

        private async Task ObserveLoadAsync(Membership member, long version, Task<PlayerRecord?> operation)
        {
            try
            {
                PlayerRecord? record = null;
                Exception? failure = null;
                try
                {
                    record = await operation.ConfigureAwait(false);
                    if (record != null && (!string.Equals(record.Name, member.Name, StringComparison.Ordinal) ||
                        !float.IsFinite(record.X) || !float.IsFinite(record.Y) || record.X < 0 || record.X > 100 || record.Y < 0 || record.Y > 100))
                    {
                        throw new InvalidDataException("저장된 캐릭터 이름 또는 위치가 잘못되었습니다.");
                    }
                }
                catch (Exception error) { failure = error; RecordPersistenceError(error); }

                GameRoom destination;
                lock (_gate)
                {
                    destination = member.Room;
                    if (IsCurrent(member) && !member.Closing && member.Version == version && failure == null)
                    {
                        int roomId = record?.RoomId ?? 1;
                        if (roomId < 1 || roomId > Rooms.Count)
                        {
                            Console.WriteLine($"[Persistence] 저장된 방 {roomId}이 없어 방 1로 입장합니다.");
                            roomId = 1;
                        }
                        destination = GetRoom(roomId);
                        member.Room = destination;
                    }
                }

                TaskCompletionSource handled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                destination.Push(() =>
                {
                    try { CompleteLoad(member, destination, version, record, failure); }
                    catch (Exception error)
                    {
                        RecordPersistenceError(error);
                        destination.HandleLeave(member.Session);
                        FinalizeClose(member);
                    }
                    finally { handled.TrySetResult(); }
                });
                await handled.Task.ConfigureAwait(false);
            }
            catch (Exception error) { RecordPersistenceError(error); }
            finally { EndPersistence(); }
        }

        private void CompleteLoad(Membership member, GameRoom room, long version, PlayerRecord? record, Exception? failure)
        {
            lock (_gate)
            {
                if (!IsCurrent(member) || member.Closing || !member.Loading || member.Version != version || member.Room != room)
                {
                    return;
                }
            }
            if (HasDisconnected(member.Session) || failure != null)
            {
                if (failure != null) { member.Session.Disconnect("캐릭터 불러오기 실패"); }
                room.HandleLeave(member.Session);
                FinalizeClose(member);
                return;
            }

            bool close;
            lock (_gate)
            {
                close = !IsCurrent(member) || member.Closing || HasDisconnected(member.Session);
                if (!close)
                {
                    member.Loading = false;
                    // 응답 전 대기 요청을 먼저 게시한다. ACK 직후 요청은 이들 뒤에 들어간다.
                    while (member.Pending.TryDequeue(out Action? replay)) { replay(); }
                }
            }
            if (close)
            {
                room.HandleLeave(member.Session);
                FinalizeClose(member);
                return;
            }

            room.HandleLoadedLogin(member.Session, member.Name, record?.X ?? 0, record?.Y ?? 0);
            lock (_gate)
            {
                close = !IsCurrent(member) || member.Closing || HasDisconnected(member.Session);
            }
            if (close)
            {
                room.HandleLeave(member.Session);
                FinalizeClose(member);
            }
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
                if (member.Closing || DeferLocked(member, () => ChangeRoom(session, roomId)) || member.Transferring || roomId < 1 || roomId > Rooms.Count)
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
                FinalizeClose(member);
                return;
            }
            Player? player = source.DetachForTransfer(member.Session);
            if (player == null)
            {
                source.HandleLeave(member.Session);
                FinalizeClose(member);
                return;
            }
            lock (_gate)
            {
                if (IsCurrent(member))
                {
                    member.Snapshot = new PlayerRecord(member.Name, player.X, player.Y, destination.RoomId);
                }
            }
            destination.Push(() => CompleteTransfer(member, destination, player, version));
        }

        private void CompleteTransfer(Membership member, GameRoom destination, Player player, long version)
        {
            bool disconnected;
            lock (_gate)
            {
                if (!IsCurrent(member) || member.Closing || member.Version != version) { return; }
                disconnected = HasDisconnected(member.Session);
                if (!disconnected)
                {
                    member.Room = destination;
                    member.Transferring = false;
                }
            }
            if (disconnected)
            {
                destination.HandleLeave(member.Session);
                FinalizeClose(member);
                return;
            }
            if (!destination.AttachTransferred(player))
            {
                destination.HandleLeave(member.Session);
                FinalizeClose(member);
                return;
            }
            bool close;
            lock (_gate) { close = !IsCurrent(member) || member.Closing || HasDisconnected(member.Session); }
            if (close)
            {
                destination.HandleLeave(member.Session);
                FinalizeClose(member);
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
                member.Pending.Clear();
                GameRoom room = member.Room;
                room.Push(() => { room.HandleLeave(session); FinalizeClose(member); });
            }
        }

        private void RememberSnapshot(GameRoom room, Player player)
        {
            lock (_gate)
            {
                if (_members.TryGetValue(player.Session, out Membership? member))
                {
                    member.Snapshot = new PlayerRecord(member.Name, player.X, player.Y, room.RoomId);
                }
            }
        }

        private void FinalizeClose(Membership member)
        {
            PlayerRecord? snapshot;
            lock (_gate)
            {
                if (!IsCurrent(member) || member.Finalizing) { return; }
                member.Finalizing = true;
                snapshot = member.Snapshot;
            }
            // SaveAsync는 이 호출 안에서 DB 줄에 넣는다. 이름 해제 뒤 Task.Run으로 미루지 않는다.
            if (snapshot != null && _store != null) { QueueSave(snapshot); }
            Release(member);
        }

        private void QueueSave(PlayerRecord snapshot)
        {
            lock (_gate) { BeginPersistenceLocked(); }
            Task operation;
            try { operation = _store!.SaveAsync(snapshot); }
            catch (Exception error) { EndPersistence(error); return; }
            _ = ObserveSaveAsync(operation, snapshot);
        }

        private async Task ObserveSaveAsync(Task operation, PlayerRecord snapshot)
        {
            Exception? failure = null;
            try
            {
                await operation.ConfigureAwait(false);
                Console.WriteLine($"[Persistence] 저장 완료: {snapshot.Name} 방 {snapshot.RoomId} ({snapshot.X}, {snapshot.Y})");
            }
            catch (Exception error) { failure = error; }
            finally { EndPersistence(failure); }
        }

        private void BeginPersistenceLocked()
        {
            if (_pendingPersistence++ == 0)
            {
                _persistenceIdle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
        private void EndPersistence(Exception? error = null)
        {
            if (error != null) { RecordPersistenceError(error); }
            lock (_gate)
            {
                if (--_pendingPersistence == 0) { _persistenceIdle.TrySetResult(); }
            }
        }
        private void RecordPersistenceError(Exception error)
        {
            lock (_gate) { _persistenceErrors.Add(error); }
            Console.WriteLine($"[Persistence] {error.Message}");
        }

        public async Task DrainPersistenceAsync(CancellationToken cancellationToken = default)
        {
            while (true)
            {
                Task pending;
                lock (_gate) { pending = _persistenceIdle.Task; }
                await pending.WaitAsync(cancellationToken);
                Exception[] errors;
                lock (_gate)
                {
                    if (_pendingPersistence != 0) { continue; }
                    errors = _persistenceErrors.ToArray();
                }
                if (errors.Length != 0) { throw new AggregateException("캐릭터 저장소 작업이 실패했습니다.", errors); }
                return;
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
                return IsCurrent(member) && !member.Closing && member.Room == room && (!version.HasValue || member.Version == version.Value);
            }
        }
        private bool IsCurrent(Membership member) => _members.TryGetValue(member.Session, out Membership? current) && current == member;
        private void Release(Membership member)
        {
            lock (_gate)
            {
                if (!IsCurrent(member)) { return; }
                _members.Remove(member.Session);
                member.Pending.Clear();
                if (_names.TryGetValue(member.Name, out Membership? owner) && owner == member) { _names.Remove(member.Name); }
            }
        }
        private static bool HasDisconnected(IGameSession session) => session.IsDisconnected || session.State == GameSessionState.Disconnected;
        private static TaskCompletionSource CompletedSource()
        {
            TaskCompletionSource source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
        private sealed class Membership
        {
            public Membership(IGameSession session, string name, GameRoom room) { Session = session; Name = name; Room = room; }
            public IGameSession Session { get; }
            public string Name { get; }
            public GameRoom Room { get; set; }
            public bool Loading { get; set; }
            public bool Transferring { get; set; }
            public bool Closing { get; set; }
            public bool Finalizing { get; set; }
            public long Version { get; set; }
            public Queue<Action> Pending { get; } = new Queue<Action>();
            public PlayerRecord? Snapshot { get; set; }
        }
    }
}
