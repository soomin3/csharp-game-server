using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Common.Network;
using Game.Network;
using Game.Room;
using Common.Auth;

namespace Game.Tests
{
    public class GameSessionNetworkTests
    {
        private static readonly TokenService Tokens = new TokenService(Enumerable.Range(1, 32).Select(value => (byte)value).ToArray());
        public static IEnumerable<object[]> InvalidPackets()
        {
            yield return Case("unknown id", Build((PacketId)65000, new byte[] { 1 }));
            yield return Case("None", Build(PacketId.None, new byte[] { 1 }));
            yield return Case("server MoveNtf", new MoveNtf { Name = "Kim", X = 1, Y = 2 }.ToPacket());
            yield return Case("server PlayerEnterNtf", new PlayerEnterNtf { Name = "Kim", X = 1, Y = 2 }.ToPacket());
            yield return Case("server PlayerLeaveNtf", new PlayerLeaveNtf { Name = "Kim" }.ToPacket());
            yield return Case("server ChangeRoomNtf", new ChangeRoomNtf { RoomId = 2, X = 1, Y = 2 }.ToPacket());
            yield return Case("short ChangeRoomReq", Build(PacketId.ChangeRoomReq, new byte[3]));
            yield return Case("long ChangeRoomReq", Build(PacketId.ChangeRoomReq, new byte[5]));
            yield return Case("invalid UTF8 Login", Build(PacketId.Login, StringBody(new byte[] { 0xc3, 0x28 })));
            yield return Case("invalid UTF8 Chat", Build(PacketId.Chat, StringBody(new byte[] { 0xc3, 0x28 })));
            yield return Case("invalid UTF8 Echo", Build(PacketId.Echo, StringBody(new byte[] { 0xc3, 0x28 })));
            yield return Case("short MoveReq", Build(PacketId.MoveReq, new byte[4]));
            yield return Case("long MoveReq", Build(PacketId.MoveReq, new byte[9]));
            yield return Case("NaN", new MoveReq { X = float.NaN, Y = 1 }.ToPacket());
            yield return Case("infinity", new MoveReq { X = 1, Y = float.PositiveInfinity }.ToPacket());
            yield return Case("zero frame length", new byte[PacketHeader.Size]);

            byte[] emptyFrame = new byte[PacketHeader.Size];
            BinaryPrimitives.WriteUInt16LittleEndian(emptyFrame, PacketHeader.Size);
            yield return Case("header-only frame", emptyFrame);
            yield return Case("empty login token", new LoginReq { Token = "" }.ToPacket());
            yield return Case("name without a token", new LoginReq { Token = "Kim" }.ToPacket());

            byte[] unknown = Build((PacketId)65000, new byte[] { 1 });
            byte[] login = new LoginReq { Token = Tokens.Issue("Kim").Token }.ToPacket();
            yield return Case("bad packet followed by Login", unknown.Concat(login).ToArray());
            yield return Case("queued Login followed by bad packet", login.Concat(unknown).ToArray());
        }

        [Theory]
        [MemberData(nameof(InvalidPackets))]
        public async Task 잘못된_패킷은_실제_TCP_연결을_닫는다(string scenario, byte[] packet)
        {
            using ConnectedSockets sockets = await ConnectedSockets.CreateAsync();
            GameRoom room = new GameRoom();
            GameSession session = new GameSession(sockets.Server, new GameHandler(room, Tokens));
            Task sessionTask = session.RunAsync();

            try
            {
                await sockets.Client.GetStream().WriteAsync(packet);
                await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.True(session.IsDisconnected, scenario);
                await AssertPeerClosedAsync(sockets.Client.GetStream());
                room.Flush();
                Assert.Equal(GameSessionState.Disconnected, session.State);
                Assert.Equal(0, room.SessionCount);
            }
            finally
            {
                session.Disconnect();
                await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }

        [Fact]
        public async Task Disconnect를_여러_번_호출해도_종료_콜백은_한_번이다()
        {
            using ConnectedSockets sockets = await ConnectedSockets.CreateAsync();
            ProbeSession session = new ProbeSession(sockets.Server);
            Task sessionTask = session.RunAsync();

            session.Disconnect();
            session.Disconnect();
            session.SendPacket(new EchoRes { Message = "ignored" }.ToPacket());

            await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(session.IsDisconnected);
            Assert.Equal(1, session.DisconnectedCount);
            await AssertPeerClosedAsync(sockets.Client.GetStream());
        }

        [Fact]
        public async Task 상대가_닫으면_종료_콜백과_연결_정리를_한_번_실행한다()
        {
            using ConnectedSockets sockets = await ConnectedSockets.CreateAsync();
            ProbeSession session = new ProbeSession(sockets.Server);
            Task sessionTask = session.RunAsync();

            sockets.Client.Close();

            await sessionTask.WaitAsync(TimeSpan.FromSeconds(5));
            session.Disconnect();
            Assert.True(session.IsDisconnected);
            Assert.Equal(1, session.DisconnectedCount);
        }

        private static object[] Case(string scenario, byte[] packet) => new object[] { scenario, packet };

        private static byte[] StringBody(byte[] bytes)
        {
            byte[] body = new byte[sizeof(int) + bytes.Length];
            BinaryPrimitives.WriteInt32LittleEndian(body, bytes.Length);
            bytes.CopyTo(body, sizeof(int));
            return body;
        }

        private static byte[] Build(PacketId id, byte[] body)
        {
            byte[] packet = new byte[PacketHeader.Size + body.Length];
            BinaryPrimitives.WriteUInt16LittleEndian(packet, (ushort)packet.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(PacketHeader.LengthSize), (ushort)id);
            body.CopyTo(packet, PacketHeader.Size);
            return packet;
        }

        private static async Task AssertPeerClosedAsync(NetworkStream stream)
        {
            try
            {
                int received = await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, received);
            }
            catch (IOException)
            {
                // TCP may report a reset instead of EOF when the peer rejects input.
            }
        }

        private sealed class ProbeSession : TSession
        {
            public int DisconnectedCount { get; private set; }

            public ProbeSession(TcpClient client) : base(client)
            {
            }

            protected override void OnSessionConnected()
            {
            }

            protected override void OnSessionDisconnected()
            {
                DisconnectedCount++;
            }

            protected override void OnPacket(PacketId id, byte[] buffer, int offset, int length)
            {
            }
        }

        private sealed class ConnectedSockets : IDisposable
        {
            public TcpClient Client { get; }
            public TcpClient Server { get; }

            private ConnectedSockets(TcpClient client, TcpClient server)
            {
                Client = client;
                Server = server;
            }

            public static async Task<ConnectedSockets> CreateAsync()
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();
                TcpClient client = new TcpClient();
                try
                {
                    Task<TcpClient> acceptTask = listener.AcceptTcpClientAsync();
                    await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
                    TcpClient server = await acceptTask.WaitAsync(TimeSpan.FromSeconds(5));
                    return new ConnectedSockets(client, server);
                }
                catch
                {
                    client.Dispose();
                    throw;
                }
                finally
                {
                    listener.Stop();
                }
            }

            public void Dispose()
            {
                Client.Dispose();
                Server.Dispose();
            }
        }
    }
}
