using Common.Network;
using Game.Network;
using Game.Room;

namespace Game.Tests
{
    public class VisibilityTests
    {
        [Fact]
        public void 먼_플레이어에게는_이동과_채팅을_보내지_않는다()
        {
            var (room, handler, kim, lee) = CreateSeparatedPlayers();

            handler.ResMove(lee, new MoveReq { X = 60f, Y = 60f });
            room.Flush();
            room.Tick();
            handler.ResChat(lee, "far away");
            room.Flush();

            Assert.Empty(kim.SentPackets);
            Assert.Single(Packets(lee, PacketId.MoveNtf));
            Assert.Single(Packets(lee, PacketId.Chat));
            Assert.Equal(2, room.SessionCount);
        }

        [Fact]
        public void 접근과_이탈은_양쪽에_입장과_퇴장을_알리고_숨겨진_이동은_보내지_않는다()
        {
            var (room, handler, kim, lee) = CreateSeparatedPlayers();

            handler.ResMove(lee, new MoveReq { X = 10f, Y = 10f });
            room.Flush();
            room.Tick();

            PlayerEnterNtf leeEnter = Enter(Assert.Single(Packets(kim, PacketId.PlayerEnterNtf)));
            Assert.Equal("Lee", leeEnter.Name);
            Assert.Equal(10f, leeEnter.X);
            Assert.Equal(10f, leeEnter.Y);
            Assert.Equal("Kim", Enter(Assert.Single(Packets(lee, PacketId.PlayerEnterNtf))).Name);
            Assert.Empty(Packets(kim, PacketId.MoveNtf));
            Assert.Single(Packets(lee, PacketId.MoveNtf));

            Clear(kim, lee);
            handler.ResMove(lee, new MoveReq { X = 20f, Y = 20f });
            room.Flush();
            room.Tick();

            Assert.Equal("Lee", Leave(Assert.Single(Packets(kim, PacketId.PlayerLeaveNtf))).Name);
            Assert.Equal("Kim", Leave(Assert.Single(Packets(lee, PacketId.PlayerLeaveNtf))).Name);
            Assert.Empty(Packets(kim, PacketId.MoveNtf));
            Assert.Empty(Packets(kim, PacketId.PlayerEnterNtf));
            Assert.Single(Packets(lee, PacketId.MoveNtf));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void 같은_틱에_서로_위치를_바꿔도_이동_순서와_무관하게_계속_보이지_않는다(bool reverse)
        {
            var (room, handler, kim, lee) = CreateSeparatedPlayers();

            PushMoves(handler, kim, new MoveReq { X = 50f, Y = 50f },
                lee, new MoveReq { X = 0f, Y = 0f }, reverse);
            room.Flush();
            room.Tick();

            AssertOnlyOwnMove(kim, "Kim", 50f, 50f);
            AssertOnlyOwnMove(lee, "Lee", 0f, 0f);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void 같은_틱의_두_이동은_모두_확정한_최종_위치로_입장을_알린다(bool reverse)
        {
            var (room, handler, kim, lee) = CreateSeparatedPlayers();

            PushMoves(handler, kim, new MoveReq { X = 50f, Y = 50f },
                lee, new MoveReq { X = 60f, Y = 60f }, reverse);
            room.Flush();
            room.Tick();

            PlayerEnterNtf leeEnter = Enter(Assert.Single(Packets(kim, PacketId.PlayerEnterNtf)));
            Assert.Equal("Lee", leeEnter.Name);
            Assert.Equal(60f, leeEnter.X);
            Assert.Equal(60f, leeEnter.Y);
            PlayerEnterNtf kimEnter = Enter(Assert.Single(Packets(lee, PacketId.PlayerEnterNtf)));
            Assert.Equal("Kim", kimEnter.Name);
            Assert.Equal(50f, kimEnter.X);
            Assert.Equal(50f, kimEnter.Y);

            Assert.Equal("Kim", Move(Assert.Single(Packets(kim, PacketId.MoveNtf))).Name);
            Assert.Equal("Lee", Move(Assert.Single(Packets(lee, PacketId.MoveNtf))).Name);
            Assert.Empty(Packets(kim, PacketId.PlayerLeaveNtf));
            Assert.Empty(Packets(lee, PacketId.PlayerLeaveNtf));
        }

        [Fact]
        public void 한_틱_안에_잠깐_가까워졌다가_돌아오면_중간_위치는_보이지_않는다()
        {
            var (room, handler, kim, lee) = CreateSeparatedPlayers();

            handler.ResMove(lee, new MoveReq { X = 10f, Y = 10f });
            handler.ResMove(lee, new MoveReq { X = 50f, Y = 50f });
            room.Flush();
            room.Tick();

            Assert.Empty(kim.SentPackets);
            AssertOnlyOwnMove(lee, "Lee", 50f, 50f);
        }

        [Fact]
        public void 좌표_100은_마지막_칸에_속하여_80의_플레이어와_서로_보인다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResLogin(lee, "Lee");
            room.Flush();
            Clear(kim, lee);

            handler.ResMove(kim, new MoveReq { X = 80f, Y = 80f });
            handler.ResMove(lee, new MoveReq { X = 100f, Y = 100f });
            room.Flush();
            room.Tick();
            handler.ResChat(lee, "edge");
            room.Flush();

            Assert.Contains(Packets(kim, PacketId.MoveNtf), packet => Move(packet).Name == "Lee");
            Assert.Contains(Packets(lee, PacketId.MoveNtf), packet => Move(packet).Name == "Kim");
            Assert.Single(Packets(kim, PacketId.Chat));
            Assert.Empty(Packets(kim, PacketId.PlayerLeaveNtf));
            Assert.Empty(Packets(lee, PacketId.PlayerLeaveNtf));
        }

        [Fact]
        public void 틱_전_입장은_아직_확정되지_않은_이동_위치를_보내지_않는다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResMove(kim, new MoveReq { X = 50f, Y = 50f });
            handler.ResLogin(lee, "Lee");
            room.Flush();

            PlayerEnterNtf kimEnter = Enter(Assert.Single(Packets(lee, PacketId.PlayerEnterNtf)));
            Assert.Equal("Kim", kimEnter.Name);
            Assert.Equal(0f, kimEnter.X);
            Assert.Equal(0f, kimEnter.Y);
            Clear(kim, lee);

            room.Tick();

            Assert.Equal("Kim", Leave(Assert.Single(Packets(lee, PacketId.PlayerLeaveNtf))).Name);
            Assert.Empty(Packets(lee, PacketId.MoveNtf));
        }

        [Fact]
        public void 연결_종료를_틱이_먼저_발견해도_퇴장_일감에서_중복_알리지_않는다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResLogin(lee, "Lee");
            room.Flush();
            Clear(kim, lee);

            lee.Disconnect();
            room.Tick();

            Assert.Equal("Lee", Leave(Assert.Single(kim.SentPackets)).Name);
            Assert.Empty(lee.SentPackets);

            handler.OnDisconnect(lee);
            room.Flush();

            Assert.Single(kim.SentPackets);
            Assert.Equal(1, room.SessionCount);
        }

        [Fact]
        public void 틱_전_채팅은_이동_예정_위치가_아닌_현재_확정_시야로_보낸다()
        {
            var (room, handler, kim, lee) = CreateSeparatedPlayers();

            handler.ResMove(lee, new MoveReq { X = 10f, Y = 10f });
            handler.ResChat(lee, "before tick");
            room.Flush();

            Assert.Empty(kim.SentPackets);
            Assert.Single(Packets(lee, PacketId.Chat));

            room.Tick();
            Clear(kim, lee);
            handler.ResChat(lee, "after tick");
            room.Flush();

            Assert.Single(Packets(kim, PacketId.Chat));
            Assert.Single(Packets(lee, PacketId.Chat));
        }

        [Fact]
        public void 멀리_있는_플레이어는_새_입장과_퇴장_알림도_보내지_않는다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResMove(kim, new MoveReq { X = 80f, Y = 80f });
            room.Flush();
            room.Tick();
            Clear(kim);

            handler.ResLogin(lee, "Lee");
            room.Flush();

            Assert.Empty(kim.SentPackets);
            Assert.Equal(PacketId.Login, IdOf(Assert.Single(lee.SentPackets)));
            Assert.Equal(2, room.SessionCount);

            handler.OnDisconnect(kim);
            room.Flush();

            Assert.Single(lee.SentPackets);
            Assert.Equal(1, room.SessionCount);
        }

        private static (GameRoom, GameHandler, FakeGameSession, FakeGameSession) CreateSeparatedPlayers()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResLogin(lee, "Lee");
            room.Flush();
            handler.ResMove(lee, new MoveReq { X = 50f, Y = 50f });
            room.Flush();
            room.Tick();
            Clear(kim, lee);
            return (room, handler, kim, lee);
        }

        private static void PushMoves(GameHandler handler, FakeGameSession kim, MoveReq kimMove,
            FakeGameSession lee, MoveReq leeMove, bool reverse)
        {
            if (reverse)
            {
                handler.ResMove(lee, leeMove);
                handler.ResMove(kim, kimMove);
            }
            else
            {
                handler.ResMove(kim, kimMove);
                handler.ResMove(lee, leeMove);
            }
        }

        private static void AssertOnlyOwnMove(FakeGameSession session, string name, float x, float y)
        {
            byte[] packet = Assert.Single(session.SentPackets);
            Assert.Equal(PacketId.MoveNtf, IdOf(packet));
            MoveNtf move = Move(packet);
            Assert.Equal(name, move.Name);
            Assert.Equal(x, move.X);
            Assert.Equal(y, move.Y);
        }

        private static void Clear(params FakeGameSession[] sessions)
        {
            foreach (FakeGameSession session in sessions)
            {
                session.SentPackets.Clear();
            }
        }

        private static PacketId IdOf(byte[] packet) =>
            (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize);

        private static IEnumerable<byte[]> Packets(FakeGameSession session, PacketId id) =>
            session.SentPackets.Where(packet => IdOf(packet) == id);

        private static PlayerEnterNtf Enter(byte[] packet)
        {
            PlayerEnterNtf? value = PlayerEnterNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
            Assert.NotNull(value);
            return value;
        }

        private static PlayerLeaveNtf Leave(byte[] packet)
        {
            PlayerLeaveNtf? value = PlayerLeaveNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
            Assert.NotNull(value);
            return value;
        }

        private static MoveNtf Move(byte[] packet)
        {
            MoveNtf? value = MoveNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
            Assert.NotNull(value);
            return value;
        }
    }
}
