namespace Common.Network
{
    public struct PacketHeader
    {
        public const int LengthSize = 2;
        public const int IdSize = 2;
        public const int Size = LengthSize + IdSize;

        public ushort Length;
        public PacketId Id;

        public PacketHeader(ushort length, PacketId id)
        {
            Length = length;
            Id = id;
        }
    }
}
