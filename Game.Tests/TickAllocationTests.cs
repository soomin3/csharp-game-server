using Common.Network;
using Game.Network;
using Game.Room;

namespace Game.Tests
{
    public class TickAllocationTests
    {
        [Fact]
        public void 움직임이_없는_틱은_시야를_다시_계산해도_관리_힙에_할당하지_않는다()
        {
            var (room, sessions) = CreateCrowd(40);
            // JIT과 재사용 집합의 용량을 먼저 맞춘다.
            for (int i = 0; i < 3; i++)
            {
                room.Tick();
            }
            Clear(sessions);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10; i++)
            {
                room.Tick();
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
            Assert.All(sessions, session => Assert.Empty(session.SentPackets));
        }

        [Fact]
        public void 같은_틱에_여러_관찰자에게_새로_보인_플레이어의_입장_패킷은_한_번만_만든다()
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            FakeGameSession park = new FakeGameSession();
            handler.ResLogin(kim, "Kim");
            handler.ResLogin(lee, "Lee");
            handler.ResLogin(park, "Park");
            room.Flush();
            handler.ResMove(park, new MoveReq { X = 90f, Y = 90f });
            room.Flush();
            room.Tick();
            Clear(kim, lee, park);

            handler.ResMove(park, new MoveReq { X = 5f, Y = 5f });
            room.Flush();
            room.Tick();

            byte[] toKim = Assert.Single(Packets(kim, PacketId.PlayerEnterNtf));
            byte[] toLee = Assert.Single(Packets(lee, PacketId.PlayerEnterNtf));
            Assert.Same(toKim, toLee);
            PlayerEnterNtf? enter = PlayerEnterNtf.ToValue(toKim, PacketHeader.Size, toKim.Length - PacketHeader.Size);
            Assert.NotNull(enter);
            Assert.Equal("Park", enter.Name);
            Assert.Equal(5f, enter.X);
            Assert.Equal(5f, enter.Y);
        }

        // 맵 전체에 흩어 두어 관찰자마다 시야 크기가 다르게 한다.
        private static (GameRoom, FakeGameSession[]) CreateCrowd(int count)
        {
            GameRoom room = new GameRoom();
            GameHandler handler = new GameHandler(room);
            FakeGameSession[] sessions = new FakeGameSession[count];
            for (int i = 0; i < count; i++)
            {
                sessions[i] = new FakeGameSession();
                handler.ResLogin(sessions[i], $"P{i}");
            }
            room.Flush();
            for (int i = 0; i < count; i++)
            {
                handler.ResMove(sessions[i], new MoveReq { X = i % 8 * 12f + 1f, Y = i / 8 * 12f + 1f });
            }
            room.Flush();
            room.Tick();
            return (room, sessions);
        }

        private static void Clear(params FakeGameSession[] sessions)
        {
            foreach (FakeGameSession session in sessions)
            {
                session.SentPackets.Clear();
            }
        }

        private static IEnumerable<byte[]> Packets(FakeGameSession session, PacketId id) =>
            session.SentPackets.Where(packet => (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize) == id);
    }
}
