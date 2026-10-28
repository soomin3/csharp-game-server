using Common.Network;
using Game.Network;
using Game.Room;

namespace Game.Tests
{
    public class SessionDefenseTests
    {
        [Fact]
        public void 로그인_전_채팅은_아무에게도_가지_않고_연결을_끊는다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession stranger = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            room.Flush();
            kim.SentPackets.Clear();

            handler.ResChat(stranger, "hi");
            handler.ResLogin(stranger, "LateLogin");
            room.Flush();

            Assert.True(stranger.IsDisconnected);
            Assert.Equal(GameSessionState.Disconnected, stranger.State);
            Assert.Empty(stranger.SentPackets);
            Assert.Empty(kim.SentPackets);
            Assert.Equal(1, room.SessionCount);
        }

        [Fact]
        public void 로그인_전_이동은_연결을_끊고_틱_알림도_보내지_않는다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession stranger = new FakeGameSession();

            handler.ResMove(stranger, new MoveReq { X = 1f, Y = 2f });
            room.Flush();
            room.Tick();

            Assert.True(stranger.IsDisconnected);
            Assert.Empty(stranger.SentPackets);
            Assert.Equal(0, room.SessionCount);
        }

        [Fact]
        public void 같은_일감_묶음에서_로그인_다음_채팅은_허용한다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();

            handler.ResLogin(kim, "Kim");
            handler.ResChat(kim, "hi");
            room.Flush();

            Assert.False(kim.IsDisconnected);
            Assert.Equal(GameSessionState.LoggedIn, kim.State);
            Assert.Single(kim.SentPackets.Where(packet => IdOf(packet) == PacketId.Chat));
        }

        [Fact]
        public void 같은_세션의_재로그인은_끊고_기존_이름으로_한_번_퇴장한다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResLogin(lee, "Lee");
            room.Flush();
            kim.SentPackets.Clear();
            lee.SentPackets.Clear();

            handler.ResLogin(kim, "Other");
            handler.ResChat(kim, "must not arrive");
            handler.OnDisconnect(kim);
            room.Flush();

            Assert.True(kim.IsDisconnected);
            Assert.Equal("Kim", kim.Name);
            Assert.Equal(1, room.SessionCount);
            Assert.Empty(kim.SentPackets);
            byte[] packet = Assert.Single(lee.SentPackets);
            Assert.Equal(PacketId.PlayerLeaveNtf, IdOf(packet));
            PlayerLeaveNtf? ntf = PlayerLeaveNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
            Assert.NotNull(ntf);
            Assert.Equal("Kim", ntf.Name);
        }

        [Fact]
        public void 같은_이름의_새_세션만_끊고_기존_플레이어는_유지한다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession original = new FakeGameSession();
            FakeGameSession duplicate = new FakeGameSession();
            handler.ResLogin(original, "Kim");
            room.Flush();
            original.SentPackets.Clear();

            handler.ResLogin(duplicate, "Kim");
            room.Flush();

            Assert.True(duplicate.IsDisconnected);
            Assert.False(original.IsDisconnected);
            Assert.Equal(GameSessionState.LoggedIn, original.State);
            Assert.Equal(1, room.SessionCount);
            Assert.Empty(original.SentPackets);
            Assert.Empty(duplicate.SentPackets);
        }

        [Fact]
        public void 이름_비교는_대소문자를_구분한다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession upper = new FakeGameSession();
            FakeGameSession lower = new FakeGameSession();

            handler.ResLogin(upper, "Kim");
            handler.ResLogin(lower, "kim");
            room.Flush();

            Assert.False(upper.IsDisconnected);
            Assert.False(lower.IsDisconnected);
            Assert.Equal(2, room.SessionCount);
        }

        public static IEnumerable<object[]> InvalidNames()
        {
            foreach (string name in new[] { "", "   ", " Kim", "Kim ", "Kim\n", "Kim\u001b", new string('a', 65), new string('김', 22), "\ud800" })
            {
                yield return new object[] { name };
            }
        }

        [Theory]
        [MemberData(nameof(InvalidNames))]
        public void 잘못된_이름은_연결을_끊고_입장시키지_않는다(string name)
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession session = new FakeGameSession();

            handler.ResLogin(session, name);
            room.Flush();

            Assert.True(session.IsDisconnected);
            Assert.Equal(GameSessionState.Disconnected, session.State);
            Assert.Equal(0, room.SessionCount);
            Assert.Empty(session.SentPackets);
        }

        [Fact]
        public void 이름_제한은_UTF8_바이트로_계산한다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession session = new FakeGameSession();

            handler.ResLogin(session, new string('김', 21));
            room.Flush();

            Assert.False(session.IsDisconnected);
            Assert.Equal(1, room.SessionCount);
        }

        [Fact]
        public void 이미_연결이_끊긴_세션의_대기_중_로그인은_무시한다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession session = new FakeGameSession();
            handler.ResLogin(session, "Kim");
            session.Disconnect();

            room.Flush();

            Assert.Equal(0, room.SessionCount);
            Assert.Empty(session.SentPackets);
        }

        [Fact]
        public void 공백뿐인_채팅은_무시하고_연결은_유지한다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            room.Flush();
            kim.SentPackets.Clear();

            handler.ResChat(kim, "   ");
            room.Flush();

            Assert.False(kim.IsDisconnected);
            Assert.Empty(kim.SentPackets);
        }

        [Theory]
        [InlineData(1025, false)]
        [InlineData(1, true)]
        public void 너무_길거나_제어문자가_있는_채팅은_연결을_끊는다(int size, bool control)
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            room.Flush();
            kim.SentPackets.Clear();

            handler.ResChat(kim, control ? "\u001b" : new string('a', size));
            room.Flush();

            Assert.True(kim.IsDisconnected);
            Assert.Equal(0, room.SessionCount);
            Assert.Empty(kim.SentPackets);
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void 유한하지_않은_이동_좌표는_연결을_끊는다(float x)
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            room.Flush();
            kim.SentPackets.Clear();

            handler.ResMove(kim, new MoveReq { X = x, Y = 1f });
            room.Flush();
            room.Tick();

            Assert.True(kim.IsDisconnected);
            Assert.Equal(0, room.SessionCount);
            Assert.Empty(kim.SentPackets);
        }

        private static PacketId IdOf(byte[] packet) =>
            (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize);
    }
}
