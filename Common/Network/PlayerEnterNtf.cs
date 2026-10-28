using System.Text;

namespace Common.Network
{
    public class PlayerEnterNtf
    {
        public string Name { get; set; } = string.Empty;
        public float X { get; set; }
        public float Y { get; set; }

        public byte[] ToPacket()
        {
            BAWriter writer = new BAWriter(sizeof(int) + Encoding.UTF8.GetByteCount(Name) + sizeof(float) + sizeof(float));
            writer.PutString(Name);
            writer.PutFloat(X);
            writer.PutFloat(Y);
            return writer.ToPacket(PacketId.PlayerEnterNtf);
        }

        public static PlayerEnterNtf? ToValue(byte[] buffer, int offset, int length)
        {
            BAReader reader = new BAReader(buffer, offset, length);
            PlayerEnterNtf ntf = new PlayerEnterNtf();
            ntf.Name = reader.GetString();
            ntf.X = reader.GetFloat();
            ntf.Y = reader.GetFloat();
            if (!reader.IsComplete)
            {
                return null;
            }

            return ntf;
        }
    }
}
