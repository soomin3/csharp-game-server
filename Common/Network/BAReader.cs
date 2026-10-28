using System.Buffers.Binary;
using System.Text;

namespace Common.Network
{
    public struct BAReader
    {
        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);
        private readonly byte[] _bytes;
        private readonly int _end;
        private int _offset;

        public bool IsValid { get; private set; }
        public bool IsComplete => IsValid && _offset == _end;

        public BAReader(byte[] bytes, int offset, int length)
        {
            _bytes = bytes ?? Array.Empty<byte>();
            _offset = 0;
            _end = 0;
            IsValid = bytes != null && offset >= 0 && length >= 0 &&
                offset <= bytes.Length && length <= bytes.Length - offset;
            if (IsValid)
            {
                _offset = offset;
                _end = offset + length;
            }
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
            if (!float.IsFinite(value))
            {
                IsValid = false;
            }
            return value;
        }

        public string GetString()
        {
            int length = GetInt();
            if (!CanRead(length))
            {
                return string.Empty;
            }

            try
            {
                string value = StrictUtf8.GetString(_bytes, _offset, length);
                _offset += length;
                return value;
            }
            catch (DecoderFallbackException)
            {
                IsValid = false;
                return string.Empty;
            }
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
