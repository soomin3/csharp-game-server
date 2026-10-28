using Common.Network;

namespace Game.Tests
{
    public class PacketTests
    {
        [Fact]
        public void ChangeRoomReq를_쓰고_읽으면_방_번호가_유지된다()
        {
            byte[] bytes = new ChangeRoomReq { RoomId = 3 }.ToPacket();
            ChangeRoomReq? read = ChangeRoomReq.ToValue(bytes, PacketHeader.Size, bytes.Length - PacketHeader.Size);

            Assert.NotNull(read);
            Assert.Equal(3, read.RoomId);
            Assert.Equal(PacketId.ChangeRoomReq, (PacketId)BitConverter.ToUInt16(bytes, PacketHeader.LengthSize));
        }

        [Fact]
        public void ChangeRoomNtf를_쓰고_읽으면_방과_확정_위치가_유지된다()
        {
            byte[] bytes = new ChangeRoomNtf { RoomId = 2, X = 3.5f, Y = 7f }.ToPacket();
            ChangeRoomNtf? read = ChangeRoomNtf.ToValue(bytes, PacketHeader.Size, bytes.Length - PacketHeader.Size);

            Assert.NotNull(read);
            Assert.Equal(2, read.RoomId);
            Assert.Equal(3.5f, read.X);
            Assert.Equal(7f, read.Y);
            Assert.Equal(PacketId.ChangeRoomNtf, (PacketId)BitConverter.ToUInt16(bytes, PacketHeader.LengthSize));
        }

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

        [Fact]
        public void PlayerEnterNtf를_쓰고_읽으면_같은_값이_나온다()
        {
            PlayerEnterNtf original = new PlayerEnterNtf { Name = "김철수", X = 3.5f, Y = 7f };

            byte[] packet = original.ToPacket();
            PlayerEnterNtf? read = PlayerEnterNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);

            Assert.Equal(PacketId.PlayerEnterNtf, (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize));
            Assert.NotNull(read);
            Assert.Equal("김철수", read.Name);
            Assert.Equal(3.5f, read.X);
            Assert.Equal(7f, read.Y);
        }

        [Fact]
        public void PlayerLeaveNtf를_쓰고_읽으면_같은_값이_나온다()
        {
            PlayerLeaveNtf original = new PlayerLeaveNtf { Name = "김철수" };

            byte[] packet = original.ToPacket();
            PlayerLeaveNtf? read = PlayerLeaveNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);

            Assert.Equal(PacketId.PlayerLeaveNtf, (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize));
            Assert.NotNull(read);
            Assert.Equal("김철수", read.Name);
        }
    }
}
