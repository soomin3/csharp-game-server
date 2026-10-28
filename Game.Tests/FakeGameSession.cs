using Game.Network;

namespace Game.Tests
{
    public class FakeGameSession : IGameSession
    {
        public string Name { get; set; } = "(로그인 전)";
        public List<byte[]> SentPackets { get; } = new List<byte[]>();

        public void SendPacket(byte[] packet)
        {
            SentPackets.Add(packet);
        }
    }
}