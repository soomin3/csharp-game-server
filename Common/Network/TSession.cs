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

        protected TSession(TcpClient client)
        {
            _client = client;
            _stream = client.GetStream();
        }

        public async Task RunAsync()
        {
            OnSessionConnected();
            Task sendTask = SendLoopAsync();

            try
            {
                while (true)
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
            catch (IOException e)
            {
                Console.WriteLine($"[TSession] 연결 오류: {e.Message}");
            }

            _sendChannel.Writer.Complete();
            await sendTask;

            OnSessionDisconnected();
            _client.Close();
        }

        public void SendPacket(byte[] packet)
        {
            _sendChannel.Writer.TryWrite(packet);
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
            catch (IOException)
            {
            }
        }

        protected abstract void OnSessionConnected();
        protected abstract void OnSessionDisconnected();
        protected abstract void OnPacket(PacketId id, byte[] buffer, int offset, int length);
    }
}