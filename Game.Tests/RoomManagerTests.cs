using Common.Job;
using Common.Network;
using Game.Network;
using Game.Room;

namespace Game.Tests
{
    public class RoomManagerTests
    {
        [Fact]
        public async Task 로그인_전_방_이동은_연결을_끊고_명단이나_예약을_만들지_않는다()
        {
            await using RoomScheduler scheduler = new RoomScheduler();
            RoomManager manager = new RoomManager(scheduler);
            FakeGameSession stranger = new FakeGameSession();

            new GameHandler(manager).ResChangeRoom(stranger, 2);
            await scheduler.DrainAsync();

            Assert.True(stranger.IsDisconnected);
            Assert.Equal(GameSessionState.Disconnected, stranger.State);
            Assert.Equal(0, manager.ReservedNameCount);
            Assert.All(manager.Rooms, room => Assert.Equal(0, room.SessionCount));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        public async Task 없는_방_요청은_현재_방을_유지하고_연결도_유지한다(int roomId)
        {
            await using RoomScheduler scheduler = new RoomScheduler();
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            FakeGameSession kim = new FakeGameSession();
            handler.ResLogin(kim, "Kim"); await scheduler.DrainAsync(); Clear(kim);

            handler.ResChangeRoom(kim, roomId); await scheduler.DrainAsync();

            Assert.False(kim.IsDisconnected);
            Assert.Equal(1, manager.GetCurrentRoomId(kim));
            Assert.Single(manager.Rooms.Where(room => room.SessionCount == 1));
            Assert.Empty(kim.SentPackets);
        }

        [Fact]
        public async Task 방_이동은_옛_시야를_비우고_확정_위치를_보존하며_pending은_버린다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(2, 2);
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession lee = new FakeGameSession();
            FakeGameSession park = new FakeGameSession();
            handler.ResLogin(kim, "Kim"); handler.ResLogin(lee, "Lee"); handler.ResLogin(park, "Park");
            await scheduler.DrainAsync();
            handler.ResChangeRoom(park, 2);
            await scheduler.DrainAsync();
            handler.ResMove(kim, new MoveReq { X = 3.5f, Y = 7f });
            manager.GetRoom(1).Push(manager.GetRoom(1).Tick);
            await scheduler.DrainAsync();
            Clear(kim, lee, park);

            handler.ResMove(kim, new MoveReq { X = 99f, Y = 99f });
            handler.ResChangeRoom(kim, 2);
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, manager.GetCurrentRoomId(kim));
            Assert.False(manager.IsTransferring(kim));
            Assert.Equal(1, manager.GetRoom(1).SessionCount);
            Assert.Equal(2, manager.GetRoom(2).SessionCount);
            Assert.Equal("Kim", Leave(Assert.Single(lee.SentPackets)).Name);
            PlayerEnterNtf entered = Enter(Assert.Single(park.SentPackets));
            Assert.Equal("Kim", entered.Name); Assert.Equal(3.5f, entered.X); Assert.Equal(7f, entered.Y);
            Assert.Equal(new[] { PacketId.PlayerLeaveNtf, PacketId.ChangeRoomNtf, PacketId.PlayerEnterNtf }, kim.SentPackets.Select(IdOf));
            Assert.Equal("Lee", Leave(kim.SentPackets[0]).Name);
            ChangeRoomNtf changed = Changed(kim.SentPackets[1]);
            Assert.Equal(2, changed.RoomId); Assert.Equal(3.5f, changed.X); Assert.Equal(7f, changed.Y);
            Assert.Equal("Park", Enter(kim.SentPackets[2]).Name);

            manager.GetRoom(2).Push(manager.GetRoom(2).Tick);
            await scheduler.DrainAsync();
            Assert.Equal(3, kim.SentPackets.Count);
        }

        [Fact]
        public async Task 이름은_방_사이에서도_중복을_거절하고_퇴장_후에만_재사용한다()
        {
            await using RoomScheduler scheduler = new RoomScheduler();
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            FakeGameSession kim = new FakeGameSession();
            FakeGameSession duplicate = new FakeGameSession();
            handler.ResLogin(kim, "Kim"); await scheduler.DrainAsync();
            handler.ResChangeRoom(kim, 3); await scheduler.DrainAsync();
            handler.ResLogin(duplicate, "Kim"); await scheduler.DrainAsync();
            Assert.True(duplicate.IsDisconnected);
            Assert.False(kim.IsDisconnected);
            Assert.Equal(3, manager.GetCurrentRoomId(kim));
            Assert.Equal(1, manager.ReservedNameCount);

            handler.OnDisconnect(kim); await scheduler.DrainAsync();
            Assert.Equal(0, manager.ReservedNameCount);
            FakeGameSession replacement = new FakeGameSession();
            handler.ResLogin(replacement, "Kim"); await scheduler.DrainAsync();
            Assert.False(replacement.IsDisconnected);
            Assert.Equal(1, manager.GetCurrentRoomId(replacement));
        }

        [Fact]
        public async Task 이동_중_게임_요청은_무시하고_추가_방_이동도_시작하지_않는다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(2);
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            FakeGameSession kim = new FakeGameSession();
            handler.ResLogin(kim, "Kim"); await scheduler.DrainAsync(); Clear(kim);
            using ManualResetEventSlim started = new ManualResetEventSlim();
            using ManualResetEventSlim release = new ManualResetEventSlim();
            manager.GetRoom(2).Push(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                handler.ResChangeRoom(kim, 2);
                await manager.GetRoom(1).BarrierAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(manager.IsTransferring(kim));
                Assert.Equal(0, manager.GetRoom(1).SessionCount);
                handler.ResMove(kim, new MoveReq { X = 9, Y = 9 });
                handler.ResChat(kim, "ignored");
                handler.ResChangeRoom(kim, 3);
                handler.ResLogin(kim, "Other");
            }
            finally { release.Set(); }
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, manager.GetCurrentRoomId(kim));
            Assert.False(kim.IsDisconnected);
            Assert.Equal("Kim", kim.Name);
            ChangeRoomNtf notification = Changed(Assert.Single(kim.SentPackets));
            Assert.Equal(0f, notification.X); Assert.Equal(0f, notification.Y);
            manager.GetRoom(2).Push(manager.GetRoom(2).Tick); await scheduler.DrainAsync();
            Assert.Single(kim.SentPackets);
            handler.ResChat(kim, "after"); await scheduler.DrainAsync();
            Assert.Equal(PacketId.Chat, IdOf(kim.SentPackets[1]));
        }

        [Fact]
        public async Task 이동_중_연결을_끊으면_대상_방에_유령과_이름_예약이_남지_않는다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(2);
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            FakeGameSession kim = new FakeGameSession();
            handler.ResLogin(kim, "Kim"); await scheduler.DrainAsync(); Clear(kim);
            using ManualResetEventSlim started = new ManualResetEventSlim();
            using ManualResetEventSlim release = new ManualResetEventSlim();
            manager.GetRoom(2).Push(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                handler.ResChangeRoom(kim, 2);
                await manager.GetRoom(1).BarrierAsync().WaitAsync(TimeSpan.FromSeconds(5));
                handler.OnDisconnect(kim);
                await manager.GetRoom(1).BarrierAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            finally { release.Set(); }
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(manager.Rooms, room => Assert.Equal(0, room.SessionCount));
            Assert.Null(manager.GetCurrentRoomId(kim));
            Assert.Equal(0, manager.ReservedNameCount);
            Assert.Empty(kim.SentPackets);
            Assert.Equal(GameSessionState.Disconnected, kim.State);
        }

        [Fact]
        public async Task 같은_방_요청도_현재_위치_응답을_받고_전체_퇴장을_비울_수_있다()
        {
            await using RoomScheduler scheduler = new RoomScheduler();
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            FakeGameSession kim = new FakeGameSession();
            handler.ResLogin(kim, "Kim"); handler.ResChangeRoom(kim, 1);
            await scheduler.DrainAsync();
            Assert.Equal(1, Changed(kim.SentPackets.Single(packet => IdOf(packet) == PacketId.ChangeRoomNtf)).RoomId);
            manager.DisconnectAll(); await scheduler.DrainAsync();
            Assert.All(manager.Rooms, room => Assert.Equal(0, room.SessionCount));
            Assert.Equal(0, manager.ReservedNameCount);
        }

        [Fact]
        public async Task 종료_콜백보다_먼저_끊김을_알아도_attach를_취소하고_예약을_푼다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(2);
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            FakeGameSession kim = new FakeGameSession();
            handler.ResLogin(kim, "Kim"); await scheduler.DrainAsync(); Clear(kim);
            using ManualResetEventSlim started = new ManualResetEventSlim();
            using ManualResetEventSlim release = new ManualResetEventSlim();
            manager.GetRoom(2).Push(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            try
            {
                handler.ResChangeRoom(kim, 2);
                await manager.GetRoom(1).BarrierAsync().WaitAsync(TimeSpan.FromSeconds(5));
                kim.Disconnect();
            }
            finally { release.Set(); }
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.All(manager.Rooms, room => Assert.Equal(0, room.SessionCount));
            Assert.Equal(0, manager.ReservedNameCount);
            Assert.Null(manager.GetCurrentRoomId(kim));
            Assert.Empty(kim.SentPackets);
        }

        [Fact]
        public async Task 방_응답을_보내는_도중_끊겨도_다른_사람에게_유령_입장을_알리지_않는다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(2);
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            FakeGameSession park = new FakeGameSession();
            DisconnectOnRoomResponse kim = new DisconnectOnRoomResponse();
            handler.ResLogin(park, "Park"); await scheduler.DrainAsync();
            handler.ResChangeRoom(park, 2); await scheduler.DrainAsync(); Clear(park);
            handler.ResLogin(kim, "Kim"); await scheduler.DrainAsync();

            handler.ResChangeRoom(kim, 2); await scheduler.DrainAsync();

            Assert.Equal(0, manager.GetRoom(1).SessionCount);
            Assert.Equal(1, manager.GetRoom(2).SessionCount);
            Assert.Null(manager.GetCurrentRoomId(kim));
            Assert.Equal(1, manager.ReservedNameCount);
            Assert.Empty(park.SentPackets);
        }

        [Fact]
        public async Task 방_알림을_받자마자_보낸_첫_채팅은_목적지_입장_뒤에_처리한다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(2);
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            RoomResponseSession kim = new RoomResponseSession();
            FakeGameSession park = new FakeGameSession();
            handler.ResLogin(kim, "Kim"); handler.ResLogin(park, "Park");
            await scheduler.DrainAsync();
            handler.ResChangeRoom(park, 2); await scheduler.DrainAsync();
            Clear(kim, park);
            kim.OnRoomResponse = notification =>
            {
                if (notification.RoomId == 2) { handler.ResChat(kim, "immediately after room response"); }
            };

            handler.ResChangeRoom(kim, 2);
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(2, manager.GetCurrentRoomId(kim));
            Assert.False(kim.IsDisconnected);
            Assert.Equal(new[] { PacketId.ChangeRoomNtf, PacketId.PlayerEnterNtf, PacketId.Chat }, kim.SentPackets.Select(IdOf));
            byte[] packet = Assert.Single(park.SentPackets.Where(value => IdOf(value) == PacketId.Chat));
            ChatNtf? chat = ChatNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size);
            Assert.NotNull(chat);
            Assert.Equal("Kim: immediately after room response", chat.Message);
        }

        [Fact]
        public async Task 방_알림_직후_다음_이동을_요청하면_그_이동의_busy를_덮어쓰지_않는다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(2);
            RoomManager manager = new RoomManager(scheduler);
            GameHandler handler = new GameHandler(manager);
            RoomResponseSession kim = new RoomResponseSession();
            handler.ResLogin(kim, "Kim"); await scheduler.DrainAsync();
            bool? nextTransferWasBusy = null;
            kim.OnRoomResponse = notification =>
            {
                if (notification.RoomId == 2)
                {
                    manager.GetRoom(2).Push(() => nextTransferWasBusy = manager.IsTransferring(kim));
                    handler.ResChangeRoom(kim, 3);
                }
            };

            handler.ResChangeRoom(kim, 2);
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(nextTransferWasBusy);
            Assert.False(kim.IsDisconnected);
            Assert.Equal(3, manager.GetCurrentRoomId(kim));
            Assert.False(manager.IsTransferring(kim));
            Assert.Equal(0, manager.GetRoom(1).SessionCount);
            Assert.Equal(0, manager.GetRoom(2).SessionCount);
            Assert.Equal(1, manager.GetRoom(3).SessionCount);
        }

        private sealed class RoomResponseSession : FakeGameSession, IGameSession
        {
            public Action<ChangeRoomNtf>? OnRoomResponse { get; set; }

            void IGameSession.SendPacket(byte[] packet)
            {
                base.SendPacket(packet);
                if (!IsDisconnected && IdOf(packet) == PacketId.ChangeRoomNtf)
                {
                    OnRoomResponse?.Invoke(Changed(packet));
                }
            }
        }

        private sealed class DisconnectOnRoomResponse : IGameSession
        {
            public string Name { get; set; } = string.Empty;
            public GameSessionState State { get; set; } = GameSessionState.Connected;
            public bool IsDisconnected { get; private set; }
            public void Disconnect(string? reason = null) { IsDisconnected = true; }
            public void SendPacket(byte[] packet)
            {
                if (IdOf(packet) == PacketId.ChangeRoomNtf) { Disconnect(); }
            }
        }

        private static void Clear(params FakeGameSession[] sessions) { foreach (FakeGameSession session in sessions) { session.SentPackets.Clear(); } }
        private static PacketId IdOf(byte[] packet) => (PacketId)BitConverter.ToUInt16(packet, PacketHeader.LengthSize);
        private static PlayerEnterNtf Enter(byte[] packet) { Assert.Equal(PacketId.PlayerEnterNtf, IdOf(packet)); return PlayerEnterNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size)!; }
        private static PlayerLeaveNtf Leave(byte[] packet) { Assert.Equal(PacketId.PlayerLeaveNtf, IdOf(packet)); return PlayerLeaveNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size)!; }
        private static ChangeRoomNtf Changed(byte[] packet) { Assert.Equal(PacketId.ChangeRoomNtf, IdOf(packet)); return ChangeRoomNtf.ToValue(packet, PacketHeader.Size, packet.Length - PacketHeader.Size)!; }
    }
}
