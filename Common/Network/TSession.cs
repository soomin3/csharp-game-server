using System.Net.Sockets;
using System.Threading.Channels;

namespace Common.Network
{
    public abstract class TSession
    {
        private readonly TcpClient _client;
        private readonly NetworkStream _stream;
        private readonly byte[] _readBuffer = new byte[4096];
        private readonly byte[] _recvBuffer = new byte[ushort.MaxValue];
        private readonly Channel<byte[]> _sendChannel = Channel.CreateUnbounded<byte[]>();
        private int _recvSize;
        private int _disconnected;

        public bool IsDisconnected => Volatile.Read(ref _disconnected) != 0;

        protected TSession(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public async Task RunAsync()
        {
            Task sendTask = Task.CompletedTask;

            try
            {
                OnSessionConnected();
                sendTask = SendLoopAsync();

                while (!IsDisconnected)
                {
                    int received = await _stream.ReadAsync(_readBuffer);
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
            if (!IsDisconnected)
            {
                _sendChannel.Writer.TryWrite(packet);
            }
        }

        public void Disconnect()
        {
            if (Interlocked.Exchange(ref _disconnected, 1) != 0)
            {
                return;
            }

            _sendChannel.Writer.TryComplete();
            _client.Close();
        }

        private async Task SendLoopAsync()
        {
            try
            {
                await foreach (byte[] packet in _sendChannel.Reader.ReadAllAsync())
                {
                    await _stream.WriteAsync(packet);
                }
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
