using Common.Auth;
using Common.Job;
using Common.Network;
using Game.Network;
using Game.Room;

namespace Game.Tests
{
    public class WireAuthenticationTests
    {
        // 단위 테스트 전용 값이다. 실행 서버의 키는 반드시 환경변수에서 읽는다.
        private static byte[] TestKey() => Enumerable.Range(1, 32).Select(value => (byte)value).ToArray();

        [Fact]
        public void 검증기를_생략한_단일_방_핸들러는_네트워크_로그인을_거절한다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession session = new FakeGameSession();
            string token = new TokenService(TestKey()).Issue("Kim").Token;

            DispatchLogin(handler, session, token);
            room.Flush();

            Assert.True(session.IsDisconnected);
            Assert.Equal(GameSessionState.Disconnected, session.State);
            Assert.Equal(0, room.SessionCount);
            Assert.Empty(session.SentPackets);
        }

        [Fact]
        public async Task 검증기를_생략한_방_관리자_핸들러도_네트워크_로그인을_거절한다()
        {
            await using RoomScheduler scheduler = new RoomScheduler();
            RoomManager manager = new RoomManager(scheduler);
            FakeGameSession session = new FakeGameSession();

            DispatchLogin(new GameHandler(manager), session, new TokenService(TestKey()).Issue("Kim").Token);
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(session.IsDisconnected);
            Assert.Equal(GameSessionState.Disconnected, session.State);
            Assert.Equal(0, manager.ReservedNameCount);
            Assert.All(manager.Rooms, room => Assert.Equal(0, room.SessionCount));
        }

        [Theory]
        [InlineData("empty")]
        [InlineData("name only")]
        [InlineData("tampered")]
        [InlineData("expired")]
        [InlineData("wrong issuer")]
        [InlineData("wrong key")]
        [InlineData("too long")]
        public void 유효하지_않은_토큰은_이름이나_명단을_만들지_않는다(string scenario)
        {
            ManualTimeProvider clock = new ManualTimeProvider();
            TokenService tokens = new TokenService(TestKey(), timeProvider: clock);
            string token = tokens.Issue("Kim").Token;
            switch (scenario)
            {
                case "empty": token = ""; break;
                case "name only": token = "Kim"; break;
                case "tampered":
                {
                    string[] parts = token.Split('.');
                    parts[2] = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];
                    token = string.Join('.', parts);
                    break;
                }
                case "expired": clock.Now = clock.Now.AddMinutes(30); break;
                case "wrong issuer": token = new TokenService(TestKey(), "Another.Login", clock).Issue("Kim").Token; break;
                case "wrong key":
                {
                    byte[] key = TestKey();
                    key[0] ^= 0xff;
                    token = new TokenService(key, timeProvider: clock).Issue("Kim").Token;
                    break;
                }
                case "too long": token = new string('a', TokenService.MaxTokenLength + 1); break;
            }
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room, tokens);
            FakeGameSession session = new FakeGameSession();

            DispatchLogin(handler, session, token);
            room.Flush();

            Assert.True(session.IsDisconnected);
            Assert.Equal("(로그인 전)", session.Name);
            Assert.Equal(GameSessionState.Disconnected, session.State);
            Assert.Equal(0, room.SessionCount);
            Assert.Empty(session.SentPackets);
            Assert.DoesNotContain(token.Length == 0 ? "never-match-empty" : token, session.DisconnectReason ?? "");
        }

        [Fact]
        public void 정상_네트워크_로그인은_서명된_이름으로_입장하고_뒤의_채팅을_허용한다()
        {
            TokenService tokens = new TokenService(TestKey());
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room, tokens);
            FakeGameSession session = new FakeGameSession();

            DispatchLogin(handler, session, tokens.Issue("김철수").Token);
            byte[] chat = new ChatReq { Message = "hello" }.ToPacket();
            new PacketDispatcher(handler).Dispatch(session, PacketId.Chat, chat, PacketHeader.Size, chat.Length - PacketHeader.Size);
            room.Flush();

            Assert.False(session.IsDisconnected);
            Assert.Equal("김철수", session.Name);
            Assert.Equal(GameSessionState.LoggedIn, session.State);
            Assert.Equal(1, room.SessionCount);
            Assert.Single(session.SentPackets.Where(packet => IdOf(packet) == PacketId.Login));
            byte[] packet = Assert.Single(session.SentPackets.Where(packet => IdOf(packet) == PacketId.Chat));
            ChatNtf? notification = ChatNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
            Assert.NotNull(notification);
            Assert.Equal("김철수: hello", notification.Message);
        }

        [Fact]
        public async Task 정상_토큰은_방_관리자에서도_검증한_이름으로_입장한다()
        {
            await using RoomScheduler scheduler = new RoomScheduler();
            RoomManager manager = new RoomManager(scheduler);
            TokenService tokens = new TokenService(TestKey());
            GameHandler handler = new GameHandler(manager, tokens);
            FakeGameSession session = new FakeGameSession();

            DispatchLogin(handler, session, tokens.Issue("Kim").Token);
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.False(session.IsDisconnected);
            Assert.Equal("Kim", session.Name);
            Assert.Equal(GameSessionState.LoggedIn, session.State);
            Assert.Equal(1, manager.GetCurrentRoomId(session));
            Assert.Equal(1, manager.ReservedNameCount);

            handler.OnDisconnect(session);
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, manager.ReservedNameCount);
        }

        [Fact]
        public void 이미_종료한_세션은_정상_토큰으로도_다시_입장하지_않는다()
        {
            GameRoom room = new GameRoom();
            TokenService tokens = new TokenService(TestKey());
            FakeGameSession session = new FakeGameSession();
            session.Disconnect();

            DispatchLogin(new GameHandler(room, tokens), session, tokens.Issue("Kim").Token);
            room.Flush();

            Assert.Equal(0, room.SessionCount);
            Assert.Empty(session.SentPackets);
        }

        private static void DispatchLogin(GameHandler handler, IGameSession session, string token)
        {
            byte[] packet = new LoginReq { Token = token }.ToPacket();
            new PacketDispatcher(handler).Dispatch(session, PacketId.Login, packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
        }

        private static PacketId IdOf(byte[] packet) => (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize);

        private sealed class ManualTimeProvider : TimeProvider
        {
            public DateTimeOffset Now { get; set; } = new DateTimeOffset(2026, 10, 28, 0, 0, 0, TimeSpan.Zero);
            public override DateTimeOffset GetUtcNow() => Now;
        }
    }
}
