namespace Common.Network
{
    public class MoveReq
    {
        public float X { get; set; }
        public float Y { get; set; }

        public byte[] ToPacket()
        {
            BAWriter writer = new BAWriter(sizeof(float) + sizeof(float));
            writer.PutFloat(X);
            writer.PutFloat(Y);
            return writer.ToPacket(PacketId.MoveReq);
        }

        public static MoveReq? ToValue(byte[] buffer, int offset, int length)
        {
            BAReader reader = new BAReader(buffer, offset, length);
            MoveReq req = new MoveReq();
            req.X = reader.GetFloat();
            req.Y = reader.GetFloat();
            if (!reader.IsValid)
            {
                return null;
            }

            return req;
        }
    }
}