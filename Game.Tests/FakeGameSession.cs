using Game.Network;

namespace Game.Tests
{
    public class FakeGameSession : IGameSession
    {
        public string Name { get; set; } = "(로그인 전)";
        public GameSessionState State { get; set; } = GameSessionState.Connected;
        public bool IsDisconnected { get; private set; }
        public string? DisconnectReason { get; private set; }
        public List<byte[]> SentPackets { get; } = new List<byte[]>();

        public void SendPacket(byte[] packet)
        {
            if (!IsDisconnected)
            {
                SentPackets.Add(packet);
            }
        }

        public void Disconnect(string? reason = null)
        {
            if (!IsDisconnected)
            {
                DisconnectReason = reason;
            }
            IsDisconnected = true;
        }
    }
}
