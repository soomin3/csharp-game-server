using System.Net.Sockets;
using System.Text;
using Common.Network;

TcpClient client = new TcpClient();
await client.ConnectAsync("127.0.0.1", 7777);
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
await stream.WriteAsync(PacketBuilder.Build(PacketId.Login, name));

Console.WriteLine("채팅을 입력하세요. 이동은 /move x y (예: /move 3.5 7). 빈 줄을 입력하면 종료합니다.");
while (true)
{
    string? line = Console.ReadLine();
    if (string.IsNullOrEmpty(line))
    {
        break;
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

    await stream.WriteAsync(PacketBuilder.Build(PacketId.Chat, line));
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
            Console.WriteLine($"[DummyClient] 로그인 응답: {Encoding.UTF8.GetString(buffer, offset, length)}");
            break;
        case PacketId.Chat:
            Console.WriteLine($"[채팅] {Encoding.UTF8.GetString(buffer, offset, length)}");
            break;
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
    }
}
