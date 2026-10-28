using System.Buffers.Binary;
using System.Text;

namespace Common.Network
{
    public struct BAReader
    {
        private readonly byte[] _bytes;
        private readonly int _end;
        private int _offset;

        public bool IsValid { get; private set; }

        public BAReader(byte[] bytes, int offset, int length)
        {
            _bytes = bytes;
            _offset = offset;
            _end = offset + length;
            IsValid = true;
        }

        public int GetInt()
        {
            if (!CanRead(sizeof(int)))
            {
                return 0;
            }

            int value = BinaryPrimitives.ReadInt32LittleEndian(_bytes.AsSpan(_offset));
            _offset += sizeof(int);
            return value;
        }

        public float GetFloat()
        {
            if (!CanRead(sizeof(float)))
            {
                return 0;
            }

            float value = BinaryPrimitives.ReadSingleLittleEndian(_bytes.AsSpan(_offset));
            _offset += sizeof(float);
            return value;
        }

        public string GetString()
        {
            int length = GetInt();
            if (!CanRead(length))
            {
                return string.Empty;
            }

            string value = Encoding.UTF8.GetString(_bytes, _offset, length);
            _offset += length;
            return value;
        }

        private bool CanRead(int size)
        {
            if (!IsValid || size < 0 || size > _end - _offset)
            {
                IsValid = false;
                return false;
            }

            return true;
        }
    }
}