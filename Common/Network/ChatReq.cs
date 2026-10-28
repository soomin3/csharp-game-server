
namespace Common.Network
{
    public sealed class ChatReq
    {
        public string Message { get; set; } = string.Empty;

        public byte[] ToPacket()
        {
            BAWriter writer = new BAWriter(checked(sizeof(int) + System.Text.Encoding.UTF8.GetByteCount(this.Message)));
            writer.PutString(this.Message);
            return writer.ToPacket(PacketId.Chat);
        }

        public static ChatReq? ToValue(byte[] buffer, int offset, int length)
        {
            BAReader reader = new BAReader(buffer, offset, length);
            ChatReq packet = new ChatReq();
            packet.Message = reader.GetString();
            return reader.IsComplete ? packet : null;
        }
    }
}
