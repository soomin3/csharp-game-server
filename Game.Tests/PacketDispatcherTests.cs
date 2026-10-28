using System.Buffers.Binary;
using Common.Network;
using Game.Network;

namespace Game.Tests
{
    public class PacketDispatcherTests
    {
        [Theory]
        [InlineData(PacketId.Login)]
        [InlineData(PacketId.Echo)]
        [InlineData(PacketId.Chat)]
        public void 문자열_패킷을_맞는_타입과_세션으로_전달한다(PacketId id)
        {
            byte[] bytes = id switch
            {
                PacketId.Login => new LoginReq { Name = "김철수" }.ToPacket(),
                PacketId.Echo => new EchoReq { Message = "김철수" }.ToPacket(),
                _ => new ChatReq { Message = "김철수" }.ToPacket()
            };
            SpyHandler handler = new SpyHandler();
            FakeGameSession session = new FakeGameSession();
            PacketDispatcher dispatcher = new PacketDispatcher(handler);

            Assert.True(dispatcher.Dispatch(session, id, bytes, PacketHeader.Size, bytes.Length - PacketHeader.Size));

            Assert.Same(session, handler.Session);
            string text = id switch
            {
                PacketId.Login => Assert.IsType<LoginReq>(handler.Packet).Name,
                PacketId.Echo => Assert.IsType<EchoReq>(handler.Packet).Message,
                _ => Assert.IsType<ChatReq>(handler.Packet).Message
            };
            Assert.Equal("김철수", text);
            Assert.False(session.IsDisconnected);
        }

        [Fact]
        public void 이동_패킷은_숫자를_유지해서_이동_핸들러로_전달한다()
        {
            byte[] bytes = new MoveReq { X = 3.5f, Y = 7f }.ToPacket();
            SpyHandler handler = new SpyHandler();
            FakeGameSession session = new FakeGameSession();

            Assert.True(new PacketDispatcher(handler).Dispatch(session, PacketId.MoveReq, bytes, PacketHeader.Size, bytes.Length - PacketHeader.Size));

            MoveReq packet = Assert.IsType<MoveReq>(handler.Packet);
            Assert.Equal(3.5f, packet.X);
            Assert.Equal(7f, packet.Y);
        }

        [Theory]
        [InlineData(PacketId.None)]
        [InlineData(PacketId.MoveNtf)]
        [InlineData(PacketId.PlayerEnterNtf)]
        [InlineData(PacketId.PlayerLeaveNtf)]
        [InlineData((PacketId)65000)]
        public void 허용하지_않은_패킷은_핸들러_호출_없이_연결을_끊는다(PacketId id)
        {
            SpyHandler handler = new SpyHandler();
            FakeGameSession session = new FakeGameSession();

            Assert.False(new PacketDispatcher(handler).Dispatch(session, id, new byte[] { 1 }, 0, 1));

            Assert.True(session.IsDisconnected);
            Assert.NotNull(session.DisconnectReason);
            Assert.Null(handler.Packet);
        }

        [Theory]
        [InlineData(-1, 4)]
        [InlineData(0, -1)]
        [InlineData(int.MaxValue, 4)]
        [InlineData(4, int.MaxValue)]
        public void 잘못된_본문_범위는_예외_없이_연결을_끊는다(int offset, int length)
        {
            SpyHandler handler = new SpyHandler();
            FakeGameSession session = new FakeGameSession();
            byte[] bytes = new LoginReq { Name = "Kim" }.ToPacket();

            Assert.False(new PacketDispatcher(handler).Dispatch(session, PacketId.Login, bytes, offset, length));

            Assert.True(session.IsDisconnected);
            Assert.Null(handler.Packet);
        }

        [Fact]
        public void 잘못된_UTF8은_핸들러로_전달하지_않는다()
        {
            byte[] body = new byte[sizeof(int) + 2];
            BinaryPrimitives.WriteInt32LittleEndian(body, 2);
            body[sizeof(int)] = 0xc3;
            body[sizeof(int) + 1] = 0x28;
            SpyHandler handler = new SpyHandler();
            FakeGameSession session = new FakeGameSession();

            Assert.False(new PacketDispatcher(handler).Dispatch(session, PacketId.Login, body, 0, body.Length));

            Assert.True(session.IsDisconnected);
            Assert.Null(handler.Packet);
        }

        [Fact]
        public void 남는_본문이_있으면_요청을_거절한다()
        {
            byte[] bytes = new LoginReq { Name = "Kim" }.ToPacket().Concat(new byte[] { 0 }).ToArray();
            SpyHandler handler = new SpyHandler();
            FakeGameSession session = new FakeGameSession();

            Assert.False(new PacketDispatcher(handler).Dispatch(session, PacketId.Login, bytes, PacketHeader.Size, bytes.Length - PacketHeader.Size));

            Assert.True(session.IsDisconnected);
            Assert.Null(handler.Packet);
        }

        [Fact]
        public void 연결을_끊은_뒤의_패킷은_핸들러로_전달하지_않는다()
        {
            byte[] bytes = new LoginReq { Name = "Kim" }.ToPacket();
            SpyHandler handler = new SpyHandler();
            FakeGameSession session = new FakeGameSession();
            PacketDispatcher dispatcher = new PacketDispatcher(handler);
            dispatcher.Dispatch(session, PacketId.None, new byte[] { 1 }, 0, 1);

            Assert.False(dispatcher.Dispatch(session, PacketId.Login, bytes, PacketHeader.Size, bytes.Length - PacketHeader.Size));

            Assert.Null(handler.Packet);
        }

        [Fact]
        public void 구현하지_않은_핸들러의_기본_동작은_연결_종료다()
        {
            byte[] bytes = new EchoReq { Message = "hello" }.ToPacket();
            FakeGameSession session = new FakeGameSession();

            new PacketDispatcher(new EmptyHandler()).Dispatch(session, PacketId.Echo, bytes, PacketHeader.Size, bytes.Length - PacketHeader.Size);

            Assert.True(session.IsDisconnected);
            Assert.Contains("EchoReq", session.DisconnectReason);
        }

        private sealed class EmptyHandler : IPacketHandler
        {
        }

        private sealed class SpyHandler : IPacketHandler
        {
            public IGameSession? Session { get; private set; }
            public object? Packet { get; private set; }
            public void ResLogin(IGameSession session, LoginReq packet) => Record(session, packet);
            public void ResEcho(IGameSession session, EchoReq packet) => Record(session, packet);
            public void ResChat(IGameSession session, ChatReq packet) => Record(session, packet);
            public void ResMove(IGameSession session, MoveReq packet) => Record(session, packet);

            private void Record(IGameSession session, object packet)
            {
                Session = session;
                Packet = packet;
            }
        }
    }
}
