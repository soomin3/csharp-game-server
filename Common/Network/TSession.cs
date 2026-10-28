using System.Net.Sockets;

namespace Common.Network
{
    public abstract class TSession
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly byte[] _readBuffer = new byte[4096];
        private readonly byte[] _recvBuffer = new byte[ushort.MaxValue];
        private readonly OutgoingPacketQueue _outgoing;
        private int _recvSize;
        private int _disconnected;
        private int _started;

        public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;
        public int PendingSendPackets => _outgoing.PendingPackets;
        public int PendingSendBytes => _outgoing.PendingBytes;

        protected TSession(TcpClient client, SessionOptions? options = null)
        {
            _client = client;
            _stream = client.GetStream();
            _outgoing = new OutgoingPacketQueue(options);
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0)
            {
                throw new InvalidOperationException("같은 세션은 한 번만 실행할 수 있습니다.");
            }

            Task sendTask = Task.CompletedTask;
            using CancellationTokenRegistration registration = cancellationToken.Register(() => Disconnect());

            try
            {
                if (IsDisconnected)
                {
                    return;
                }

                OnSessionConnected();
                sendTask = SendLoopAsync(cancellationToken);

                while (!IsDisconnected)
                {
                    int received = await _stream.ReadAsync(_readBuffer, cancellationToken);
                    if (received == 0)
                    {
                        break;
                    }

                    FrameResult result = PacketFramer.Process(_recvBuffer, ref _recvSize, _readBuffer, received, OnPacket);
                    if (result != FrameResult.Success)
                    {
                        Console.WriteLine($"[TSession] {result} — 연결을 끊습니다.");
                        break;
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                if (!IsDisconnected)
                {
                    Console.WriteLine($"[TSession] 연결 오류: {e.Message}");
                }
            }
            finally
            {
                Disconnect();
                try
                {
                    await sendTask;
                }
                finally
                {
                    OnSessionDisconnected();
                }
            }
        }

        public void SendPacket(byte[] packet)
        {
            if (IsDisconnected || _outgoing.TryWrite(packet))
            {
                return;
            }

            Disconnect("송신 대기 한도 초과");
        }

        public void Disconnect(string? reason = null)
        {
            if (Interlocked.Exchange(ref _disconnected, 1) != 0)
            {
                return;
            }

            try
            {
                _outgoing.Abort();
            }
            finally
            {
                _client.Close();
            }

            if (reason != null)
            {
                try
                {
                    Console.WriteLine($"[TSession] {reason} — 연결을 끊습니다.");
                }
                catch (Exception error) when (error is IOException or ObjectDisposedException)
                {
                    // 로그 실패가 실제 연결 정리와 세션 작업 종료를 막지 않게 한다.
                }
            }
        }

        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            try
            {
                await foreach (OutgoingPacket packet in _outgoing.ReadAllAsync(cancellationToken))
                {
                    using (packet)
                    {
                        await _stream.WriteAsync(packet.Data, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                Disconnect();
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                Disconnect();
            }
        }

        protected abstract void OnSessionConnected();
        protected abstract void OnSessionDisconnected();
        protected abstract void OnPacket(PacketId id, byte[] buffer, int offset, int length);
    }
}
