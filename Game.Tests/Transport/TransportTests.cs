using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Common.Network;

namespace Queue.Tests
{
    public class TransportTests
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

        [Fact]
        public async Task 접속_수락만_멈춘_뒤에도_기존_세션은_통신하고_별도로_종료한다()
        {
            await using TestServer host = new TestServer();
            var (client, session) = await host.ConnectAsync();

            host.Server.StopAccepting();
            host.Server.StopAccepting();
            await host.Running.WaitAsync(Timeout);

            Assert.Equal(1, host.Server.ActiveSessionCount);
            Assert.False(session.IsDisconnected);
            await EchoAsync(client);
            using TcpClient rejected = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() =>
                rejected.ConnectAsync(IPAddress.Loopback, host.Server.BoundPort).WaitAsync(Timeout));

            await host.Server.DisconnectAllAsync().WaitAsync(Timeout);

            Assert.Equal(0, host.Server.ActiveSessionCount);
            Assert.Equal(1, session.DisconnectedCount);
            Assert.Equal(0, session.PendingSendBytes);
            Assert.Equal(0, session.PendingSendPackets);
            await ReadUntilClosedAsync(client);
        }

        [Fact]
        public async Task 서버_취소는_수락만_멈추고_기존_접속은_후속_종료까지_유지한다()
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            await using TestServer host = new TestServer(cancellationToken: cancellation.Token);
            var (client, session) = await host.ConnectAsync();

            cancellation.Cancel();
            await host.Running.WaitAsync(Timeout);

            Assert.False(session.IsDisconnected);
            Assert.Equal(1, host.Server.ActiveSessionCount);
            await EchoAsync(client);

            await host.Server.StopAsync().WaitAsync(Timeout);

            Assert.True(session.IsDisconnected);
            Assert.Equal(0, host.Server.ActiveSessionCount);
        }

        [Fact]
        public async Task 세션_취소는_읽기를_깨우고_송신_대기를_모두_해제한다()
        {
            using ConnectedSockets sockets = await ConnectedSockets.CreateAsync();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            ProbeSession session = new ProbeSession(sockets.Server);
            Task running = session.RunAsync(cancellation.Token);
            await session.Connected.Task.WaitAsync(Timeout);

            cancellation.Cancel();
            await running.WaitAsync(Timeout);

            Assert.True(session.IsDisconnected);
            Assert.Equal(1, session.DisconnectedCount);
            Assert.Equal(0, session.PendingSendBytes);
            Assert.Equal(0, session.PendingSendPackets);
            await ReadUntilClosedAsync(sockets.Client);
        }

        [Fact]
        public async Task 쌓인_패킷은_순서대로_묶어_쓰고_묶음보다_큰_패킷은_단독으로_쓴다()
        {
            using ConnectedSockets sockets = await ConnectedSockets.CreateAsync();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            ProbeSession session = new ProbeSession(sockets.Server, new SessionOptions { SendBatchBytes = 64 });
            List<byte[]> packets = new List<byte[]>();
            for (int i = 0; i < 20; i++)
            {
                // 7번과 15번은 묶음 버퍼보다 크다. 나머지는 경계를 넘는 위치가 계속 달라지게 크기를 바꾼다.
                byte[] packet = new byte[i == 7 || i == 15 ? 200 : 10 + i];
                packet.AsSpan().Fill((byte)(i + 1));
                packets.Add(packet);
                // 송신 루프 시작 전에 넣어 두어 묶음 구성이 실행 시점에 따라 달라지지 않게 한다.
                session.SendPacket(packet);
            }
            byte[] expected = packets.SelectMany(packet => packet).ToArray();

            Task running = session.RunAsync(cancellation.Token);
            byte[] received = new byte[expected.Length];
            await sockets.Client.GetStream().ReadExactlyAsync(received).AsTask().WaitAsync(Timeout);
            await WaitUntilAsync(() => session.PendingSendPackets == 0);

            Assert.Equal(expected, received);
            Assert.Equal(0, session.PendingSendBytes);
            // 60 / 31 / 200 / 57 / 43 / 47 / 200 / 53 / 57바이트의 9번 쓰기
            Assert.Equal(9, session.SocketWriteCount);
            cancellation.Cancel();
            await running.WaitAsync(Timeout);
        }

        [Fact]
        public async Task 상대_EOF를_관측하면_세션_작업을_명단에서_제거한다()
        {
            await using TestServer host = new TestServer();
            var (client, session) = await host.ConnectAsync();

            client.Close();
            await session.Disconnected.Task.WaitAsync(Timeout);
            await WaitUntilAsync(() => host.Server.ActiveSessionCount == 0);

            Assert.True(session.IsDisconnected);
            Assert.Equal(1, session.DisconnectedCount);
            Assert.Equal(0, session.PendingSendBytes);
        }

        [Fact]
        public async Task 읽지_않는_클라이언트의_송신_대기가_한도를_넘으면_연결을_끊는다()
        {
            await using TestServer host = new TestServer(new SessionOptions
            {
                MaxPendingPackets = 4,
                MaxPendingBytes = 64 * 1024,
            });
            var (client, session) = await host.ConnectAsync();
            client.ReceiveBufferSize = 1024;
            byte[] packet = new byte[32 * 1024];

            for (int i = 0; i < 10000 && !session.IsDisconnected; i++)
            {
                session.SendPacket(packet);
            }
            Assert.True(session.IsDisconnected);
            await session.Disconnected.Task.WaitAsync(Timeout);
            await host.Server.StopAsync().WaitAsync(Timeout);

            Assert.Equal(0, session.PendingSendPackets);
            Assert.Equal(0, session.PendingSendBytes);
            Assert.Equal(1, session.DisconnectedCount);
            Assert.Equal(0, host.Server.ActiveSessionCount);
            await ReadUntilClosedAsync(client);
        }

        [Fact]
        public async Task 여러_종료_호출이_겹쳐도_세션은_한_번만_정리한다()
        {
            await using TestServer host = new TestServer();
            List<ProbeSession> sessions = new List<ProbeSession>();
            for (int i = 0; i < 3; i++)
            {
                var (_, session) = await host.ConnectAsync();
                sessions.Add(session);
            }

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.Server.StopAsync())).WaitAsync(Timeout);
            await host.Running.WaitAsync(Timeout);
            host.Server.StopAccepting();
            await host.Server.DisconnectAllAsync().WaitAsync(Timeout);

            Assert.Equal(0, host.Server.ActiveSessionCount);
            Assert.All(sessions, session =>
            {
                Assert.True(session.IsDisconnected);
                Assert.Equal(1, session.DisconnectedCount);
                Assert.Equal(0, session.PendingSendPackets);
                Assert.Equal(0, session.PendingSendBytes);
            });
        }

        [Fact]
        public async Task 세션_작업의_예외를_관측하고_연결과_명단을_정리한다()
        {
            await using TestServer host = new TestServer(failOnConnect: true);
            var (client, session) = await host.ConnectAsync();
            await session.Disconnected.Task.WaitAsync(Timeout);
            await WaitUntilAsync(() => host.Server.ActiveSessionCount == 0 && host.Server.SessionFailureCount == 1);

            Assert.True(session.IsDisconnected);
            Assert.Equal(1, session.DisconnectedCount);
            await ReadUntilClosedAsync(client);
        }

        [Fact]
        public async Task 시작_전에_취소되거나_종료된_서버는_포트를_열지_않는다()
        {
            using CancellationTokenSource cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            TServer first = new TServer(0, _ => throw new InvalidOperationException());
            await first.RunAsync(cancelled.Token);
            await first.StopAsync();
            Assert.Equal(0, first.BoundPort);

            TServer second = new TServer(0, _ => throw new InvalidOperationException());
            second.StopAccepting();
            await second.StopAsync();
            await second.RunAsync();
            Assert.Equal(0, second.BoundPort);
        }

        [Fact]
        public async Task 접속과_종료가_겹쳐도_등록된_세션_작업을_남기지_않는다()
        {
            await using TestServer host = new TestServer();
            await host.ConnectAsync();
            Task[] connections = Enumerable.Range(0, 20).Select(async _ =>
            {
                using TcpClient client = new TcpClient();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, host.Server.BoundPort).WaitAsync(Timeout);
                }
                catch (SocketException)
                {
                    // 수락 종료와 겹친 접속은 거절될 수 있다.
                }
            }).ToArray();

            await host.Server.StopAsync().WaitAsync(Timeout);
            await Task.WhenAll(connections).WaitAsync(Timeout);

            Assert.Equal(0, host.Server.ActiveSessionCount);
            Assert.Equal(0, host.Server.SessionFailureCount);
            Assert.All(host.Sessions, session => Assert.Equal(1, session.DisconnectedCount));
        }

        private static byte[] EchoPacket()
        {
            byte[] packet = new byte[PacketHeader.Size + 1];
            BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)packet.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(PacketHeader.LengthSize), (ushort)PacketId.Echo);
            packet[PacketHeader.Size] = 123;
            return packet;
        }

        private static async Task EchoAsync(TcpClient client)
        {
            byte[] packet = EchoPacket();
            await client.GetStream().WriteAsync(packet);
            byte[] reply = new byte[packet.Length];
            await client.GetStream().ReadExactlyAsync(reply).AsTask().WaitAsync(Timeout);
            Assert.Equal(packet, reply);
        }

        private static async Task ReadUntilClosedAsync(TcpClient client)
        {
            using CancellationTokenSource cancellation = new CancellationTokenSource(Timeout);
            byte[] bytes = new byte[4096];
            int total = 0;
            try
            {
                while (true)
                {
                    int read = await client.GetStream().ReadAsync(bytes, cancellation.Token);
                    if (read == 0)
                    {
                        return;
                    }
                    total += read;
                    Assert.True(total < 16 * 1024 * 1024);
                }
            }
            catch (IOException)
            {
                // TCP 종료가 EOF 대신 reset으로 보고될 수도 있다.
            }
        }

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.Add(Timeout);
            while (!condition())
            {
                Assert.True(DateTimeOffset.UtcNow < deadline);
                await Task.Delay(5);
            }
        }

        private sealed class TestServer : IAsyncDisposable
        {
            private readonly Channel<ProbeSession> _created = Channel.CreateUnbounded<ProbeSession>();
            private readonly List<TcpClient> _clients = new List<TcpClient>();
            public ConcurrentBag<ProbeSession> Sessions { get; } = new ConcurrentBag<ProbeSession>();
            public TServer Server { get; }
            public Task Running { get; }

            public TestServer(SessionOptions? options = null, CancellationToken cancellationToken = default, bool failOnConnect = false)
            {
                Server = new TServer(0, client =>
                {
                    ProbeSession session = new ProbeSession(client, options, failOnConnect);
                    Sessions.Add(session);
                    _created.Writer.TryWrite(session);
                    return session;
                });
                Running = Server.RunAsync(cancellationToken);
            }

            public async Task<(TcpClient, ProbeSession)> ConnectAsync()
            {
                TcpClient client = new TcpClient();
                _clients.Add(client);
                await client.ConnectAsync(IPAddress.Loopback, Server.BoundPort).WaitAsync(Timeout);
                ProbeSession session = await _created.Reader.ReadAsync().AsTask().WaitAsync(Timeout);
                await session.Connected.Task.WaitAsync(Timeout);
                return (client, session);
            }

            public async ValueTask DisposeAsync()
            {
                try
                {
                    await Server.StopAsync().WaitAsync(Timeout);
                    await Running.WaitAsync(Timeout);
                }
                finally
                {
                    foreach (TcpClient client in _clients)
                    {
                        client.Dispose();
                    }
                }
            }
        }

        private sealed class ProbeSession : TSession
        {
            private readonly bool _failOnConnect;
            private int _disconnectedCount;
            public int DisconnectedCount => Volatile.Read(ref _disconnectedCount);
            public TaskCompletionSource<bool> Connected { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            public TaskCompletionSource<bool> Disconnected { get; } = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            public ProbeSession(TcpClient client, SessionOptions? options = null, bool failOnConnect = false) : base(client, options)
            {
                client.SendBufferSize = 1024;
                _failOnConnect = failOnConnect;
            }

            protected override void OnSessionConnected()
            {
                Connected.TrySetResult(true);
                if (_failOnConnect)
                {
                    throw new InvalidOperationException("검증용 세션 오류");
                }
            }

            protected override void OnSessionDisconnected()
            {
                Interlocked.Increment(ref _disconnectedCount);
                Disconnected.TrySetResult(true);
            }

            protected override void OnPacket(PacketId id, byte[] buffer, int offset, int length)
            {
                byte[] packet = new byte[PacketHeader.Size + length];
                BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)packet.Length);
                BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(PacketHeader.LengthSize), (ushort)id);
                buffer.AsSpan(offset, length).CopyTo(packet.AsSpan(PacketHeader.Size));
                SendPacket(packet);
            }
        }

        private sealed class ConnectedSockets : IDisposable
        {
            public TcpClient Client { get; }
            public TcpClient Server { get; }

            private ConnectedSockets(TcpClient client, TcpClient server)
            {
                Client = client;
                Server = server;
            }

            public static async Task<ConnectedSockets> CreateAsync()
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                TcpClient client = new TcpClient();
                try
                {
                    Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
                    await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
                    return new ConnectedSockets(client, await accepting.WaitAsync(Timeout));
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                finally
                {
                    listener.Stop();
                }
            }

            public void Dispose()
            {
                Client.Dispose();
                Server.Dispose();
            }
        }
    }
}
