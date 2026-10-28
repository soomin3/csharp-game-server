using Game.Network;
using Game.Room;
using Common.Network;
using System.Collections.Concurrent;
using System.Text;

namespace Game.Tests
{
    public class ConcurrencyTests
    {
        [Fact]
        public void 여러_스레드가_동시에_로그인_채팅_퇴장해도_깨지지_않는다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            ConcurrentBag<FakeGameSession> sessions = new ConcurrentBag<FakeGameSession>();

            Parallel.For(0, 8, worker =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    FakeGameSession session = new FakeGameSession();
                    sessions.Add(session);
                    handler.ResLogin(session, $"user{worker}_{i}");
                    handler.ResChat(session, "hi");
                    handler.OnDisconnect(session);
                }
            });
            room.Flush();

            Assert.Equal(0, room.SessionCount);
            Assert.Equal(8000, sessions.Count);
            Assert.All(sessions, session =>
            {
                Assert.True(session.IsDisconnected);
                Assert.Equal(GameSessionState.Disconnected, session.State);
                Assert.Single(session.SentPackets.Where(packet => IdOf(packet) == PacketId.Login));
                Assert.Contains(session.SentPackets, packet =>
                    IdOf(packet) == PacketId.Chat &&
                    Encoding.UTF8.GetString(packet, PacketHeader.Size, packet.Length - PacketHeader.Size) == $"{session.Name}: hi");
            });
        }

        private static PacketId IdOf(byte[] packet) =>
            (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize);
    }
}
