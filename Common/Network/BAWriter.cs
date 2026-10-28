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
            byte[] text = StrictUtf8.GetBytes(value);
            PutInt(text.Length);
            text.CopyTo(_bytes, _offset);
            _offset += text.Length;
        }

        public byte[] ToPacket(PacketId id)
        {
            EnsureWritable(0);
            BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(0), (ushort)_offset);
            BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(PacketHeader.LengthSize), (ushort)id);
            return _bytes.AsSpan(0, _offset).ToArray();
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
