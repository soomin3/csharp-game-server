using System.Text;

namespace Common.Network
{
    public static class PacketBuilder
    {
        public static byte[] Build(PacketId id, string message)
        {
            byte[] body = Encoding.UTF8.GetBytes(message);
            ushort length = (ushort)(PacketHeader.Size + body.Length);

            byte[] packet = new byte[length];
            BitConverter.GetBytes(length).CopyTo(packet, 0);
            BitConverter.GetBytes((ushort)id).CopyTo(packet, PacketHeader.LengthSize);
            body.CopyTo(packet, PacketHeader.Size);

            return packet;
        }
    }
}
