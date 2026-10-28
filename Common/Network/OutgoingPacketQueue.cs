using System.Threading.Channels;

namespace Common.Network
{
    public sealed class OutgoingPacketQueue : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Channel<OutgoingPacket> _channel;
        private readonly SessionOptions _options;
        private bool _completed;
        private int _pendingPackets;
        private int _pendingBytes;

        public int PendingPackets
        {
            get { lock (_gate) { return _pendingPackets; } }
        }

        public int PendingBytes
        {
            get { lock (_gate) { return _pendingBytes; } }
        }

        public OutgoingPacketQueue(SessionOptions? options = null)
        {
            _options = options ?? new SessionOptions();
            if (_options.MaxPendingPackets <= 0 || _options.MaxPendingBytes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(options), "송신 대기 패킷 수와 바이트 한도는 양수여야 합니다.");
            }

            _channel = Channel.CreateBounded<OutgoingPacket>(new BoundedChannelOptions(_options.MaxPendingPackets)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleWriter = false,
                // 종료 정리에서도 대기 항목을 꺼내므로 읽는 쪽은 여럿일 수 있다.
                SingleReader = false,
                AllowSynchronousContinuations = false,
            });
        }

        // 배열을 복사하지 않는다. 전송용 배열은 만든 뒤 다시 수정하지 않는다.
        // false이면 기다리지 말고 호출자가 해당 세션의 Disconnect()를 실행한다.
        public bool TryWrite(byte[] packet)
        {
            ArgumentNullException.ThrowIfNull(packet);
            if (packet.Length == 0)
            {
                throw new ArgumentException("빈 송신 패킷은 넣을 수 없습니다.", nameof(packet));
            }

            lock (_gate)
            {
                if (_completed || _pendingPackets >= _options.MaxPendingPackets ||
                    packet.Length > _options.MaxPendingBytes - _pendingBytes)
                {
                    return false;
                }

                OutgoingPacket item = new OutgoingPacket(this, packet);
                _pendingPackets++;
                _pendingBytes += packet.Length;
                if (_channel.Writer.TryWrite(item))
                {
                    return true;
                }

                item.Dispose();
                return false;
            }
        }

        // 읽어 갔다고 한도를 돌려주지 않는다. 쓰기 완료/실패 후 항목을 Dispose해야 한다.
        public ValueTask<OutgoingPacket> ReadAsync(CancellationToken cancellationToken = default) =>
            _channel.Reader.ReadAsync(cancellationToken);

        public IAsyncEnumerable<OutgoingPacket> ReadAllAsync(CancellationToken cancellationToken = default) =>
            _channel.Reader.ReadAllAsync(cancellationToken);

        // 새 입력을 막고, 이미 들어온 항목은 소비자가 끝까지 읽게 한다.
        public void Complete()
        {
            lock (_gate)
            {
                _completed = true;
                _channel.Writer.TryComplete();
            }
        }

        // 새 입력을 막고 아직 읽지 않은 항목을 버린다.
        // 진행 중인 쓰기의 항목은 소비자가 finally/using에서 Dispose한다.
        public void Abort()
        {
            lock (_gate)
            {
                _completed = true;
                _channel.Writer.TryComplete();
                while (_channel.Reader.TryRead(out OutgoingPacket? item))
                {
                    item.Dispose();
                }
            }
        }

        public void Dispose() => Abort();

        internal void Release(int length)
        {
            lock (_gate)
            {
                _pendingPackets--;
                _pendingBytes -= length;
            }
        }
    }

    public sealed class OutgoingPacket : IDisposable
    {
        private readonly OutgoingPacketQueue _owner;
        private byte[]? _data;

        internal OutgoingPacket(OutgoingPacketQueue owner, byte[] data)
        {
            _owner = owner;
            _data = data;
        }

        public ReadOnlyMemory<byte> Data
        {
            get
            {
                byte[]? data = Volatile.Read(ref _data);
                if (data == null)
                {
                    throw new ObjectDisposedException(nameof(OutgoingPacket));
                }
                return data;
            }
        }

        public void Dispose()
        {
            byte[]? data = Interlocked.Exchange(ref _data, null);
            if (data != null)
            {
                _owner.Release(data.Length);
            }
        }
    }
}
