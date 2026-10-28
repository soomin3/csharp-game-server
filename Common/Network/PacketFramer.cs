namespace Common.Network
{
    public enum FrameResult
    {
        Success,
        BufferOverflow,
        InvalidPacketLength,
    }
    public static class PacketFramer
    {
        public static FrameResult Process(byte[] recvBuffer, ref int recvSize, byte[] readBuffer, int size, Action<PacketId, byte[], int, int> onPacket)
        {
            if (recvSize + size > recvBuffer.Length)
            {
                recvSize = 0;
                return FrameResult.BufferOverflow;
            }

            Array.Copy(readBuffer, 0, recvBuffer, recvSize, size);
            recvSize += size;

            int readOffset = 0;

            while(recvSize >= PacketHeader.Size)
            {
                ushort packetLength = BitConverter.ToUInt16(recvBuffer, readOffset);

                if(packetLength <= PacketHeader.Size || packetLength > recvBuffer.Length)
                {
                    recvSize = 0;
                    return FrameResult.InvalidPacketLength;
                }

                if(recvSize < packetLength)
                {
                    break;
                }

                PacketId id = (PacketId)BitConverter.ToUInt16(recvBuffer, readOffset + PacketHeader.LengthSize);
                onPacket(id, recvBuffer, readOffset + PacketHeader.Size, packetLength - PacketHeader.Size);

                recvSize -= packetLength;
                readOffset += packetLength;
            }

            if(recvSize > 0 && readOffset > 0)
            {
                Array.Copy(recvBuffer, readOffset, recvBuffer, 0, recvSize);
            }

            return FrameResult.Success;
        }
    }
}
