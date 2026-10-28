using Game.Network;
using Game.Room;

namespace Game.Tests
{
    public class ConcurrencyTests
    {
        [Fact]
        public void 여러_스레드가_동시에_로그인_채팅_퇴장해도_깨지지_않는다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);

            Parallel.For(0, 8, _ =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    FakeGameSession session = new FakeGameSession();
                    handler.ResLogin(session, "user");
                    handler.ResChat(session, "hi");
                    handler.OnDisconnect(session);
                }
            });
            room.Flush();

            Assert.Equal(0, room.SessionCount);
        }
    }
}