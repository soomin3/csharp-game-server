using System.Text;

namespace Common.Network
{
    public class PlayerLeaveNtf
    {
        public string Name { get; set; } = string.Empty;

        public byte[] ToPacket()
        {
            BAWriter writer = new BAWriter(sizeof(int) + Encoding.UTF8.GetByteCount(Name));
            writer.PutString(Name);
            return writer.ToPacket(PacketId.PlayerLeaveNtf);
        }

        public static PlayerLeaveNtf? ToValue(byte[] buffer, int offset, int length)
        {
            BAReader reader = new BAReader(buffer, offset, length);
            PlayerLeaveNtf ntf = new PlayerLeaveNtf();
            ntf.Name = reader.GetString();
            if (!reader.IsValid)
            {
                return null;
            }

            return ntf;
        }
    }
}
