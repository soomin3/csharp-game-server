using Common.Network;

namespace Game.Tests
{
    public class PacketTests
    {
        [Fact]
        public void MoveNtf를_쓰고_읽으면_같은_값이_나온다()
        {
            MoveNtf original = new MoveNtf { Name = "Kim", X = 3.5f, Y = 7f };

            byte[] packet = original.ToPacket();
            MoveNtf? read = MoveNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);

            Assert.NotNull(read);
            Assert.Equal("Kim", read.Name);
            Assert.Equal(3.5f, read.X);
            Assert.Equal(7f, read.Y);
        }

        [Fact]
        public void 잘린_MoveReq는_null이_된다()
        {
            byte[] packet = new MoveReq { X = 3.5f, Y = 7f }.ToPacket();

            MoveReq? read = MoveReq.ToValue(packet, PacketHeader.Size, 4);

            Assert.Null(read);
        }
    }
}