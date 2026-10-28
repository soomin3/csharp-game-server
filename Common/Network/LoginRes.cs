
namespace Common.Network
{
    public sealed class LoginRes
    {
        public string Message { get; set; } = string.Empty;

        public byte[] ToPacket()
        {
            BAWriter writer = new BAWriter(checked(sizeof(int) + System.Text.Encoding.UTF8.GetByteCount(this.Message)));
            writer.PutString(this.Message);
            return writer.ToPacket(PacketId.Login);
        }

        public static LoginRes? ToValue(byte[] buffer, int offset, int length)
        {
            BAReader reader = new BAReader(buffer, offset, length);
            LoginRes packet = new LoginRes();
            packet.Message = reader.GetString();
            return reader.IsComplete ? packet : null;
        }
    }
}
