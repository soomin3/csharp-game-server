using System.Buffers.Binary;
using System.Text;
using Common.Network;

namespace Game.Tests
{
    public class PacketIoTests
    {
        [Fact]
        public void 모든_문자열_요청과_응답은_길이와_한글을_보존한다()
        {
            const string text = "김철수 안녕하세요";
            byte[] loginReq = new LoginReq { Token = text }.ToPacket();
            byte[] loginRes = new LoginRes { Message = text }.ToPacket();
            byte[] echoReq = new EchoReq { Message = text }.ToPacket();
            byte[] echoRes = new EchoRes { Message = text }.ToPacket();
            byte[] chatReq = new ChatReq { Message = text }.ToPacket();
            byte[] chatNtf = new ChatNtf { Message = text }.ToPacket();

            Assert.Equal(text, LoginReq.ToValue(loginReq, PacketHeader.Size, loginReq.Length - PacketHeader.Size)?.Token);
            Assert.Equal(text, LoginRes.ToValue(loginRes, PacketHeader.Size, loginRes.Length - PacketHeader.Size)?.Message);
            Assert.Equal(text, EchoReq.ToValue(echoReq, PacketHeader.Size, echoReq.Length - PacketHeader.Size)?.Message);
            Assert.Equal(text, EchoRes.ToValue(echoRes, PacketHeader.Size, echoRes.Length - PacketHeader.Size)?.Message);
            Assert.Equal(text, ChatReq.ToValue(chatReq, PacketHeader.Size, chatReq.Length - PacketHeader.Size)?.Message);
            Assert.Equal(text, ChatNtf.ToValue(chatNtf, PacketHeader.Size, chatNtf.Length - PacketHeader.Size)?.Message);
            foreach (byte[] packet in new[] { loginReq, loginRes, echoReq, echoRes, chatReq, chatNtf })
            {
                Assert.Equal(packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(packet));
                Assert.Equal(Encoding.UTF8.GetByteCount(text), BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(PacketHeader.Size)));
            }
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(ushort.MaxValue - PacketHeader.Size + 1)]
        [InlineData(int.MaxValue)]
        public void 헤더에_담을_수_없는_크기의_패킷은_만들지_않는다(int bodySize)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new BAWriter(bodySize));
        }

        [Fact]
        public void 최대_패킷_크기는_헤더에서_넘치지_않는다()
        {
            int bodySize = ushort.MaxValue - PacketHeader.Size;
            BAWriter writer = new BAWriter(bodySize);
            string text = new string('a', bodySize - sizeof(int));
            writer.PutString(text);

            byte[] packet = writer.ToPacket(PacketId.Echo);

            Assert.Equal(ushort.MaxValue, packet.Length);
            Assert.Equal(ushort.MaxValue, BinaryPrimitives.ReadUInt16LittleEndian(packet));
            Assert.Equal(text, EchoReq.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size)?.Message);
        }

        [Fact]
        public void 쓰기_용량을_넘기면_명확한_오류를_낸다()
        {
            BAWriter writer = new BAWriter(sizeof(int));
            writer.PutInt(1);

            Assert.Throws<InvalidOperationException>(() => writer.PutInt(2));
        }

        [Fact]
        public void 잘못된_UTF16_문자열은_다른_문자로_바꿔_보내지_않는다()
        {
            BAWriter writer = new BAWriter(16);
            Assert.Throws<EncoderFallbackException>(() => writer.PutString("\ud800"));
        }

        [Theory]
        [InlineData(-1, 1)]
        [InlineData(0, -1)]
        [InlineData(int.MaxValue, 1)]
        [InlineData(1, int.MaxValue)]
        public void 잘못된_읽기_범위는_유효하지_않은_리더가_된다(int offset, int length)
        {
            BAReader reader = new BAReader(new byte[4], offset, length);
            Assert.False(reader.IsValid);
            Assert.False(reader.IsComplete);
            Assert.Equal(0, reader.GetInt());
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(int.MaxValue)]
        public void 잘못된_문자열_길이는_거절한다(int stringLength)
        {
            byte[] body = new byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(body, stringLength);
            Assert.Null(LoginReq.ToValue(body, 0, body.Length));
        }

        [Theory]
        [InlineData(float.NaN)]
        [InlineData(float.PositiveInfinity)]
        [InlineData(float.NegativeInfinity)]
        public void 비유한_좌표는_패킷_읽기에서_거절한다(float x)
        {
            byte[] packet = new MoveReq { X = x, Y = 1f }.ToPacket();
            Assert.Null(MoveReq.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size));
        }

        [Fact]
        public void 서버_알림도_남는_본문을_허용하지_않는다()
        {
            byte[] move = new MoveNtf { Name = "Kim", X = 1f, Y = 2f }.ToPacket().Concat(new byte[] { 0 }).ToArray();
            byte[] enter = new PlayerEnterNtf { Name = "Kim", X = 1f, Y = 2f }.ToPacket().Concat(new byte[] { 0 }).ToArray();
            byte[] leave = new PlayerLeaveNtf { Name = "Kim" }.ToPacket().Concat(new byte[] { 0 }).ToArray();

            Assert.Null(MoveNtf.ToValue(move, PacketHeader.Size, move.Length - PacketHeader.Size));
            Assert.Null(PlayerEnterNtf.ToValue(enter, PacketHeader.Size, enter.Length - PacketHeader.Size));
            Assert.Null(PlayerLeaveNtf.ToValue(leave, PacketHeader.Size, leave.Length - PacketHeader.Size));
        }
    }
}
