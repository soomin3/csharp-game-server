using System.Net;
using System.Net.Sockets;

namespace Common.Network
{
    public class TServer
    {
        private readonly object _gate = new object();
        private readonly TcpListener _listener;
        private readonly Func<TcpClient, TSession> _sessionFactory;
        private readonly Dictionary<TSession, SessionEntry> _sessions = new Dictionary<TSession, SessionEntry>();
        private Task? _runTask;
        private bool _started;
        private bool _acceptingStopped;
        private IPEndPoint? _localEndpoint;
        private int _sessionFailureCount;

        private sealed class SessionEntry
        {
            public TSession Session { get; }
            public Task Completion { get; set; } = Task.CompletedTask;

            public SessionEntry(TSession session)
            {
                Session = session;
            }
        }

        public int ActiveSessionCount
        {
            get { lock (_gate) { return _sessions.Count; } }
        }

        public IPEndPoint? LocalEndpoint
        {
            get { lock (_gate) { return _localEndpoint; } }
        }

        public int BoundPort => LocalEndpoint?.Port ?? 0;
        public int SessionFailureCount => Volatile.Read(ref _sessionFailureCount);

        public TServer(int port, Func<TcpClient, TSession> sessionFactory)
        {
            _listener = new TcpListener(IPAddress.Any, port);
            _sessionFactory = sessionFactory;
        }

        // 이 취소 토큰은 접속 수락만 멈춘다. 접속자는 DisconnectAllAsync에서 정리한다.
        public Task RunAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_started)
                {
                    throw new InvalidOperationException("같은 서버는 한 번만 실행할 수 있습니다.");
                }
                _started = true;
                _runTask = RunAcceptLoopAsync(cancellationToken);
                return _runTask;
            }
        }

        public void StopAccepting()
        {
            lock (_gate)
            {
                if (_acceptingStopped)
                {
                    return;
                }
                _acceptingStopped = true;
                _listener.Stop();
            }
        }

        public async Task DisconnectAllAsync()
        {
            StopAccepting();
            Task? accepting;
            lock (_gate)
            {
                accepting = _runTask;
            }

            try
            {
                if (accepting != null)
                {
                    await accepting;
                }
            }
            finally
            {
                SessionEntry[] sessions;
                lock (_gate)
                {
                    sessions = _sessions.Values.ToArray();
                }
                foreach (SessionEntry entry in sessions)
                {
                    entry.Session.Disconnect();
                }
                await Task.WhenAll(sessions.Select(entry => entry.Completion));
            }
        }

        public Task StopAsync() => DisconnectAllAsync();

        private async Task RunAcceptLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                lock (_gate)
                {
                    if (_acceptingStopped)
                    {
                        return;
                    }
                    _listener.Start();
                    _localEndpoint = (IPEndPoint)_listener.LocalEndpoint;
                    Console.WriteLine($"[TServer] {_localEndpoint.Port} 포트에서 대기 시작");
                }

                using CancellationTokenRegistration registration = cancellationToken.Register(StopAccepting);
                while (!HasStoppedAccepting())
                {
                    TcpClient client = await _listener.AcceptTcpClientAsync();
                    lock (_gate)
                    {
                        if (_acceptingStopped)
                        {
                            client.Dispose();
                            break;
                        }

                        try
                        {
                            TSession session = _sessionFactory(client);
                            SessionEntry entry = new SessionEntry(session);
                            _sessions.Add(session, entry);
                            entry.Completion = ObserveSessionAsync(session);
                        }
                        catch (Exception e)
                        {
                            client.Dispose();
                            Console.WriteLine($"[TServer] 세션 생성 오류: {e.GetType().Name}");
                        }
                    }
                }
            }
            catch (Exception e) when (HasStoppedAccepting() &&
                e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
            }
            finally
            {
                StopAccepting();
            }
        }

        private bool HasStoppedAccepting()
        {
            lock (_gate)
            {
                return _acceptingStopped;
            }
        }

        private async Task ObserveSessionAsync(TSession session)
        {
            try
            {
                await session.RunAsync();
            }
            catch (Exception e)
            {
                Interlocked.Increment(ref _sessionFailureCount);
                // 예외 메시지에는 입력 원문이 들어갈 수 있으므로 타입만 기록한다.
                Console.WriteLine($"[TServer] 세션 실행 오류: {e.GetType().Name}");
            }
            finally
            {
                lock (_gate)
                {
                    _sessions.Remove(session);
                }
            }
        }
    }
}
