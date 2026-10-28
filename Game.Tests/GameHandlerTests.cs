using System.Text;
using Common.Network;
using Game.Network;
using Game.Room;

namespace Game.Tests
{
    public class GameHandlerTests
    {
        [Fact]
        public void ResLogin은_이름을_세션에_저장하고_환영_패킷을_보낸다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession session = new FakeGameSession();

            handler.ResLogin(session, "Kim");
            Assert.Empty(session.SentPackets);

            room.Flush();

            Assert.Equal("Kim", session.Name);
            byte[] packet = Assert.Single(session.SentPackets);
            Assert.Equal(PacketId.Login, IdOf(packet));
            Assert.Equal("환영합니다, Kim님", TextOf(packet));
        }

        [Fact]
        public void ResEcho는_받은_말을_그대로_돌려보낸다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession session = new FakeGameSession();

            handler.ResEcho(session, "hello");
            room.Flush();

            byte[] packet = Assert.Single(session.SentPackets);
            Assert.Equal(PacketId.Echo, IdOf(packet));
            Assert.Equal("hello", TextOf(packet));
        }

        [Fact]
        public void ResChat은_로그인한_모두에게_보낸다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResLogin(lee, "Lee");

            handler.ResChat(kim, "hi");
            room.Flush();

            Assert.Equal(2, kim.SentPackets.Count);
            Assert.Equal(PacketId.Chat, IdOf(kim.SentPackets[1]));
            Assert.Equal("Kim: hi", TextOf(kim.SentPackets[1]));
            Assert.Equal(2, lee.SentPackets.Count);
            Assert.Equal("Kim: hi", TextOf(lee.SentPackets[1]));
        }

        [Fact]
        public void 접속이_끊긴_세션에는_채팅을_보내지_않는다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResLogin(lee, "Lee");

            handler.OnDisconnect(lee);
            handler.ResChat(kim, "hi");
            room.Flush();

            Assert.Equal(2, kim.SentPackets.Count);
            Assert.Single(lee.SentPackets);
        }

        [Fact]
        public void 이동하면_모두에게_MoveNtf를_보낸다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResLogin(lee, "Lee");

            handler.ResMove(kim, new MoveReq { X = 3.5f, Y = 7f });
            room.Flush();
            room.Tick();

            byte[] packet = lee.SentPackets[1];
            Assert.Equal(PacketId.MoveNtf, IdOf(packet));
            MoveNtf? ntf = MoveNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
            Assert.NotNull(ntf);
            Assert.Equal("Kim", ntf.Name);
            Assert.Equal(3.5f, ntf.X);
            Assert.Equal(7f, ntf.Y);
        }

        [Fact]
        public void 맵_밖으로는_이동할_수_없다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            handler.ResLogin(kim, "Kim");

            handler.ResMove(kim, new MoveReq { X = 500f, Y = 7f });
            room.Flush();
            room.Tick();

            Assert.Single(kim.SentPackets);
        }

        [Fact]
        public void 한_틱_안에_여러_번_이동하면_마지막_위치만_한_번_보낸다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResLogin(lee, "Lee");

            handler.ResMove(kim, new MoveReq { X = 1f, Y = 1f });
            handler.ResMove(kim, new MoveReq { X = 2f, Y = 2f });
            handler.ResMove(kim, new MoveReq { X = 3f, Y = 3f });
            room.Flush();

            Assert.Single(kim.SentPackets);
            Assert.Single(lee.SentPackets);

            room.Tick();

            foreach (FakeGameSession session in new[] { kim, lee })
            {
                Assert.Equal(2, session.SentPackets.Count);
                byte[] packet = session.SentPackets[1];
                Assert.Equal(PacketId.MoveNtf, IdOf(packet));
                MoveNtf? ntf = MoveNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
                Assert.NotNull(ntf);
                Assert.Equal("Kim", ntf.Name);
                Assert.Equal(3f, ntf.X);
                Assert.Equal(3f, ntf.Y);
            }

            room.Tick();

            Assert.Equal(2, kim.SentPackets.Count);
            Assert.Equal(2, lee.SentPackets.Count);
        }

        private static PacketId IdOf(byte[] packet) => (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize);

        private static string TextOf(byte[] packet) => Encoding.UTF8.GetString(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
    }
}
