
namespace Common.Network
{
    public sealed class LoginReq
    {
        public string Name { get; set; } = string.Empty;

        public byte[] ToPacket()
        {
            BAWriter writer = new BAWriter(checked(sizeof(int) + System.Text.Encoding.UTF8.GetByteCount(this.Name)));
            writer.PutString(this.Name);
            return writer.ToPacket(PacketId.Login);
        }

        public static LoginReq? ToValue(byte[] buffer, int offset, int length)
        {
            BAReader reader = new BAReader(buffer, offset, length);
            LoginReq packet = new LoginReq();
            packet.Name = reader.GetString();
            return reader.IsComplete ? packet : null;
        }
    }
}
