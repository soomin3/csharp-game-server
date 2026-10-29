using System.Buffers.Binary;
using System.Text;

namespace Common.Network
{
    public struct BAWriter
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly byte[] _bytes;
        private int _offset;

        public BAWriter(int bodySize)
        {
            if (bodySize < 0 || bodySize > ushort.MaxValue - PacketHeader.Size)
            {
                throw new ArgumentOutOfRangeException(nameof(bodySize), "Packet size must fit the ushort frame header.");
            }
            _bytes = new byte[PacketHeader.Size + bodySize];
            _offset = PacketHeader.Size;
        }

        public void PutInt(int value)
        {
            EnsureWritable(sizeof(int));
            BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(_offset), value);
            _offset += sizeof(int);
        }

        public void PutFloat(float value)
        {
            EnsureWritable(sizeof(float));
            BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(_offset), value);
            _offset += sizeof(float);
        }

        public void PutString(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            int byteCount = StrictUtf8.GetByteCount(value);
            EnsureWritable(checked(sizeof(int) + byteCount));
            PutInt(byteCount);
            // 임시 배열 없이 패킷 버퍼에 바로 인코딩한다.
            _offset += StrictUtf8.GetBytes(value, _bytes.AsSpan(_offset, byteCount));
        }

        public byte[] ToPacket(PacketId id)
        {
            EnsureWritable(0);
            BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(0), (ushort)_offset);
            BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(PacketHeader.LengthSize), (ushort)id);
            // 생성 코드는 본문 크기를 정확히 계산하므로 보통 버퍼를 그대로 넘기고, 남는 공간이 있을 때만 잘라 복사한다.
            return _offset == _bytes.Length ? _bytes : _bytes.AsSpan(0, _offset).ToArray();
        }

        private void EnsureWritable(int size)
        {
            if (_bytes == null || size < 0 || size > _bytes.Length - _offset)
            {
                throw new InvalidOperationException("Packet writer capacity was exceeded or the writer was not initialized.");
            }
        }
    }
}
