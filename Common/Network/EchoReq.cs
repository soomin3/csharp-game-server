
namespace Common.Network
{
    public sealed class EchoReq
    {
        public string Message { get; set; } = string.Empty;

        public byte[] ToPacket()
        {
            BAWriter writer = new BAWriter(checked(sizeof(int) + System.Text.Encoding.UTF8.GetByteCount(this.Message)));
            writer.PutString(this.Message);
            return writer.ToPacket(PacketId.Echo);
        }

        public static EchoReq? ToValue(byte[] buffer, int offset, int length)
        {
            BAReader reader = new BAReader(buffer, offset, length);
            EchoReq packet = new EchoReq();
            packet.Message = reader.GetString();
            return reader.IsComplete ? packet : null;
        }
    }
}
