using System.Net.Sockets;
using Common.Network;
using System.Net.Http.Json;
using System.Text.Json;

int portIndex = Array.IndexOf(args, "--port");
int port = 7777;
if (portIndex >= 0 && (portIndex + 1 >= args.Length || !int.TryParse(args[portIndex + 1], out port) || port < 1 || port > 65535))
{
    throw new ArgumentException("--port 뒤에 1~65535 포트 번호가 필요합니다.");
}
int loginUrlIndex = Array.IndexOf(args, "--login-url");
string loginUrl = "http://127.0.0.1:5000/login";
if (loginUrlIndex >= 0)
{
    if (loginUrlIndex + 1 >= args.Length)
    {
        throw new ArgumentException("--login-url 뒤에 HTTP 로그인 주소가 필요합니다.");
    }
    loginUrl = args[loginUrlIndex + 1];
}
if (!Uri.TryCreate(loginUrl, UriKind.Absolute, out Uri? parsedLoginUrl) ||
    (parsedLoginUrl.Scheme != "http" && parsedLoginUrl.Scheme != "https"))
{
    throw new ArgumentException("--login-url에는 http 또는 https 주소가 필요합니다.");
}

Console.Write("이름: ");
string? name = Console.ReadLine();
if (string.IsNullOrEmpty(name))
{
    name = "손님";
}

string token;
try
{
    using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    using HttpResponseMessage response = await http.PostAsJsonAsync(parsedLoginUrl, new { name });
    if (!response.IsSuccessStatusCode)
    {
        Console.Error.WriteLine($"[DummyClient] HTTP 로그인 거절: {(int)response.StatusCode}. 이름을 확인하세요.");
        return 1;
    }
    using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    token = result.RootElement.GetProperty("token").GetString()
        ?? throw new InvalidDataException("로그인 응답에 토큰이 없습니다.");
}
catch (Exception error) when (error is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidDataException)
{
    Console.Error.WriteLine($"[DummyClient] HTTP 로그인 실패: {error.GetType().Name}");
    return 1;
}

using TcpClient client = new TcpClient();
await client.ConnectAsync("127.0.0.1", port);
Console.WriteLine("[DummyClient] 접속 성공");

NetworkStream stream = client.GetStream();

byte[] readBuffer = new byte[4096];
byte[] recvBuffer = new byte[ushort.MaxValue];
int recvSize = 0;

_ = ReceiveLoopAsync();

await stream.WriteAsync(new LoginReq { Token = token }.ToPacket());

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
return 0;

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
