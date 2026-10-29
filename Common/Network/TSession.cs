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
        private readonly int _sendBatchBytes;
        private long _sessionSocketWrites;
        private int _recvSize;
        private int _disconnected;
        private int _started;
        private static long _packetsSent;
        private static long _bytesSent;
        private static long _socketWrites;

        public static long TotalPacketsSent => Interlocked.Read(ref _packetsSent);
        public static long TotalBytesSent => Interlocked.Read(ref _bytesSent);
        public static long TotalSocketWrites => Interlocked.Read(ref _socketWrites);

        public long SocketWriteCount => Interlocked.Read(ref _sessionSocketWrites);
        public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;
        public int PendingSendPackets => _outgoing.PendingPackets;
        public int PendingSendBytes => _outgoing.PendingBytes;

        protected TSession(TcpClient client, SessionOptions? options = null)
        {
            _client = client;
            _stream = client.GetStream();
            _outgoing = new OutgoingPacketQueue(options);
            _sendBatchBytes = (options ?? new SessionOptions()).SendBatchBytes;
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

        // 이미 쌓인 패킷을 한 버퍼에 모아 소켓 쓰기 한 번으로 보낸다. 틱 하나에서 관찰자에게
        // 여러 알림이 몰리므로 패킷마다 쓰던 시스템 호출 수가 묶음 수로 줄어든다.
        // 묶은 패킷은 쓰기가 끝난 뒤 Dispose해 진행 중인 쓰기도 송신 한도에 계속 포함한다.
        private async Task SendLoopAsync(CancellationToken cancellationToken)
        {
            byte[] batch = new byte[_sendBatchBytes];
            List<OutgoingPacket> batched = new List<OutgoingPacket>();
            OutgoingPacket? next = null;
            try
            {
                while (next != null || await _outgoing.WaitToReadAsync(cancellationToken))
                {
                    int length = 0;
                    while (next != null || _outgoing.TryRead(out next))
                    {
                        ReadOnlyMemory<byte> data = next.Data;
                        if (data.Length > batch.Length - length)
                        {
                            if (length > 0)
                            {
                                // 남는 공간에 들어가지 않으면 다음 묶음의 첫 패킷으로 남긴다.
                                break;
                            }

                            // 묶음 버퍼보다 큰 패킷은 복사하지 않고 단독으로 쓴다.
                            await WriteAsync(data, 1, cancellationToken);
                            next.Dispose();
                            next = null;
                            continue;
                        }

                        data.Span.CopyTo(batch.AsSpan(length));
                        length += data.Length;
                        batched.Add(next);
                        next = null;
                    }

                    if (length > 0)
                    {
                        await WriteAsync(batch.AsMemory(0, length), batched.Count, cancellationToken);
                        DisposeAll(batched);
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
            finally
            {
                next?.Dispose();
                DisposeAll(batched);
            }
        }

        private async ValueTask WriteAsync(ReadOnlyMemory<byte> data, int packetCount, CancellationToken cancellationToken)
        {
            await _stream.WriteAsync(data, cancellationToken);
            Interlocked.Increment(ref _sessionSocketWrites);
            Interlocked.Increment(ref _socketWrites);
            Interlocked.Add(ref _packetsSent, packetCount);
            Interlocked.Add(ref _bytesSent, data.Length);
        }

        private static void DisposeAll(List<OutgoingPacket> packets)
        {
            foreach (OutgoingPacket packet in packets)
            {
                packet.Dispose();
            }
            packets.Clear();
        }

        protected abstract void OnSessionConnected();
        protected abstract void OnSessionDisconnected();
        protected abstract void OnPacket(PacketId id, byte[] buffer, int offset, int length);
    }
}
