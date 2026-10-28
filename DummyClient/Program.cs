using System.Net.Sockets;
using Common.Network;

TcpClient client = new TcpClient();
int portIndex = Array.IndexOf(args, "--port");
int port = 7777;
if (portIndex >= 0 && (portIndex + 1 >= args.Length || !int.TryParse(args[portIndex + 1], out port) || port < 1 || port > 65535))
{
    throw new ArgumentException("--port 뒤에 1~65535 포트 번호가 필요합니다.");
}
await client.ConnectAsync("127.0.0.1", port);
Console.WriteLine("[DummyClient] 접속 성공");

NetworkStream stream = client.GetStream();

byte[] readBuffer = new byte[4096];
byte[] recvBuffer = new byte[ushort.MaxValue];
int recvSize = 0;

_ = ReceiveLoopAsync();

Console.Write("이름: ");
string? name = Console.ReadLine();
if (string.IsNullOrEmpty(name))
{
    name = "손님";
}
await stream.WriteAsync(new LoginReq { Name = name }.ToPacket());

Console.WriteLine("채팅을 입력하세요. 이동은 /move x y, 방 이동은 /room 번호. 빈 줄을 입력하면 종료합니다.");
while (true)
{
    string? line = Console.ReadLine();
    if (string.IsNullOrEmpty(line))
    {
        break;
    }

    if (line.StartsWith("/room "))
    {
        string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2 && int.TryParse(parts[1], out int roomId) && roomId > 0)
        {
            await stream.WriteAsync(new ChangeRoomReq { RoomId = roomId }.ToPacket());
        }
        else
        {
            Console.WriteLine("사용법: /room 번호   (예: /room 2)");
        }

        continue;
    }

    if (line.StartsWith("/move "))
    {
        string[] parts = line.Split(' ');
        if (parts.Length == 3 && float.TryParse(parts[1], out float x) && float.TryParse(parts[2], out float y))
        {
            MoveReq req = new MoveReq { X = x, Y = y };
            await stream.WriteAsync(req.ToPacket());
        }
        else
        {
            Console.WriteLine("사용법: /move x y   (예: /move 3.5 7)");
        }

        continue;
    }

    await stream.WriteAsync(new ChatReq { Message = line }.ToPacket());
}

client.Close();

async Task ReceiveLoopAsync()
{
    while (true)
    {
        int received = await stream.ReadAsync(readBuffer);
        if (received == 0)
        {
            break;
        }

        PacketFramer.Process(recvBuffer, ref recvSize, readBuffer, received, OnPacket);
    }
}

void OnPacket(PacketId id, byte[] buffer, int offset, int length)
{
    switch (id)
    {
        case PacketId.Login:
        {
            LoginRes? response = LoginRes.ToValue(buffer, offset, length);
            if (response != null)
            {
                Console.WriteLine($"[DummyClient] 로그인 응답: {response.Message}");
            }
            break;
        }
        case PacketId.Echo:
        {
            EchoRes? response = EchoRes.ToValue(buffer, offset, length);
            if (response != null)
            {
                Console.WriteLine($"[에코] {response.Message}");
            }
            break;
        }
        case PacketId.Chat:
        {
            ChatNtf? notification = ChatNtf.ToValue(buffer, offset, length);
            if (notification != null)
            {
                Console.WriteLine($"[채팅] {notification.Message}");
            }
            break;
        }
        case PacketId.MoveNtf:
        {
            MoveNtf? ntf = MoveNtf.ToValue(buffer, offset, length);
            if (ntf != null)
            {
                Console.WriteLine($"[이동] {ntf.Name} → ({ntf.X}, {ntf.Y})");
            }

            break;
        }
        case PacketId.PlayerEnterNtf:
        {
            PlayerEnterNtf? ntf = PlayerEnterNtf.ToValue(buffer, offset, length);
            if (ntf != null)
            {
                Console.WriteLine($"[입장] {ntf.Name} → ({ntf.X}, {ntf.Y})");
            }

            break;
        }
        case PacketId.PlayerLeaveNtf:
        {
            PlayerLeaveNtf? ntf = PlayerLeaveNtf.ToValue(buffer, offset, length);
            if (ntf != null)
            {
                Console.WriteLine($"[퇴장] {ntf.Name}");
            }

            break;
        }
        case PacketId.ChangeRoomNtf:
        {
            ChangeRoomNtf? ntf = ChangeRoomNtf.ToValue(buffer, offset, length);
            if (ntf != null)
            {
                Console.WriteLine($"[방] {ntf.RoomId} → ({ntf.X}, {ntf.Y})");
            }

            break;
        }
    }
}
