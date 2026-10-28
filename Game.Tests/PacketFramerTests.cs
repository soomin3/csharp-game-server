using System.Buffers.Binary;
using Common.Network;

namespace Game.Tests
{
    public class PacketFramerTests
    {
        [Fact]
        public void 헤더와_본문이_나뉘어_와도_완성된_패킷만_전달한다()
        {
            byte[] packet = new MoveReq { X = 3.5f, Y = 7f }.ToPacket();
            byte[] receiveBuffer = new byte[ushort.MaxValue];
            int receiveSize = 0;
            List<byte[]> bodies = new List<byte[]>();
            void OnPacket(PacketId id, byte[] buffer, int offset, int length)
            {
                Assert.Equal(PacketId.MoveReq, id);
                bodies.Add(buffer.AsSpan(offset, length).ToArray());
            }

            Assert.Equal(FrameResult.Success, PacketFramer.Process(receiveBuffer, ref receiveSize, packet[..2], 2, OnPacket));
            Assert.Empty(bodies);
            Assert.Equal(FrameResult.Success, PacketFramer.Process(receiveBuffer, ref receiveSize, packet[2..6], 4, OnPacket));
            Assert.Empty(bodies);
            Assert.Equal(FrameResult.Success, PacketFramer.Process(receiveBuffer, ref receiveSize, packet[6..], packet.Length - 6, OnPacket));

            byte[] body = Assert.Single(bodies);
            MoveReq? value = MoveReq.ToValue(body, 0, body.Length);
            Assert.NotNull(value);
            Assert.Equal(3.5f, value.X);
            Assert.Equal(7f, value.Y);
            Assert.Equal(0, receiveSize);
        }

        [Fact]
        public void 여러_패킷이_합쳐_와도_각각_순서대로_전달한다()
        {
            byte[] first = new MoveReq { X = 1f, Y = 2f }.ToPacket();
            byte[] second = new MoveReq { X = 3f, Y = 4f }.ToPacket();
            byte[] input = first.Concat(second).ToArray();
            byte[] receiveBuffer = new byte[ushort.MaxValue];
            int receiveSize = 0;
            List<float> positions = new List<float>();

            FrameResult result = PacketFramer.Process(receiveBuffer, ref receiveSize, input, input.Length,
                (id, buffer, offset, length) =>
                {
                    Assert.Equal(PacketId.MoveReq, id);
                    MoveReq? value = MoveReq.ToValue(buffer, offset, length);
                    Assert.NotNull(value);
                    positions.Add(value.X);
                });

            Assert.Equal(FrameResult.Success, result);
            Assert.Equal(new[] { 1f, 3f }, positions);
            Assert.Equal(0, receiveSize);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(PacketHeader.Size)]
        public void 본문이_없는_잘못된_길이는_전달하지_않는다(int declaredLength)
        {
            byte[] input = new byte[PacketHeader.Size];
            BinaryPrimitives.WriteUInt16LittleEndian(input, (ushort)declaredLength);
            byte[] receiveBuffer = new byte[ushort.MaxValue];
            int receiveSize = 0;
            bool delivered = false;

            FrameResult result = PacketFramer.Process(receiveBuffer, ref receiveSize, input, input.Length,
                (_, _, _, _) => delivered = true);

            Assert.Equal(FrameResult.InvalidPacketLength, result);
            Assert.False(delivered);
            Assert.Equal(0, receiveSize);
        }
    }
}
