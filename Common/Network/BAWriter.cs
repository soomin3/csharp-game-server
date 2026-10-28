using System.Buffers.Binary;
using System.Text;

namespace Common.Network
{
    public struct BAWriter
    {
        private readonly byte[] _bytes;
        private int _offset;

        public BAWriter(int bodySize)
        {
            _bytes = new byte[PacketHeader.Size + bodySize];
            _offset = PacketHeader.Size;
        }

        public void PutInt(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_bytes.AsSpan(_offset), value);
            _offset += sizeof(int);
        }

        public void PutFloat(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_bytes.AsSpan(_offset), value);
            _offset += sizeof(float);
        }

        public void PutString(string value)
        {
            byte[] text = Encoding.UTF8.GetBytes(value);
            PutInt(text.Length);
            text.CopyTo(_bytes, _offset);
            _offset += text.Length;
        }

        public byte[] ToPacket(PacketId id)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(0), (ushort)_offset);
            BinaryPrimitives.WriteUInt16LittleEndian(_bytes.AsSpan(PacketHeader.LengthSize), (ushort)id);
            return _bytes.AsSpan(0, _offset).ToArray();
        }
    }
}