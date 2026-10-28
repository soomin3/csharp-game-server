using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Common.Network;

namespace DummyClient
{
    internal static class DummyClientApp
    {
        private enum ReceiveResult { ServerClosed, ProtocolError, IoError, Cancelled }

        public static async Task<int> RunAsync(ClientOptions options, CancellationToken stopping)
        {
            using TcpClient client = new TcpClient();
            CancellationTokenSource? connection = null;
            Task<ReceiveResult>? receiveTask = null;
            try
            {
                Console.Write("이름: ");
                string? name = await Task.Run(static () => Console.ReadLine()).WaitAsync(stopping);
                if (name == null) { return 0; }
                if (name.Length == 0) { name = "손님"; }

                string token = await LoginAsync(options.LoginUrl, name, stopping);
                await client.ConnectAsync(options.Host, options.Port, stopping);
                Console.WriteLine("[DummyClient] 접속 성공");
                NetworkStream stream = client.GetStream();
                connection = CancellationTokenSource.CreateLinkedTokenSource(stopping);
                receiveTask = ReceiveAsync(stream, connection);
                await stream.WriteAsync(new LoginReq { Token = token }.ToPacket(), connection.Token);
                Console.WriteLine("채팅을 입력하세요. 이동은 /move x y, 방 이동은 /room 번호. 빈 줄 또는 Ctrl+C로 종료합니다.");
                Task cancelled = Task.Delay(Timeout.InfiniteTimeSpan, connection.Token);

                while (true)
                {
                    // 콘솔의 ReadLineAsync도 동기로 막힐 수 있으므로 백그라운드에서 읽는다.
                    // 서버가 먼저 끊기면 이 입력 작업을 기다리지 않고 종료한다.
                    Task<string?> input = Task.Run(static () => Console.ReadLine());
                    await Task.WhenAny(input, receiveTask, cancelled);
                    stopping.ThrowIfCancellationRequested();
                    if (receiveTask.IsCompleted || connection.IsCancellationRequested)
                    {
                        PrintReceiveEnd(await receiveTask);
                        return 1;
                    }

                    string? line = await input;
                    if (string.IsNullOrEmpty(line)) { return 0; }
                    byte[]? packet = MakeRequest(line);
                    if (packet != null)
                    {
                        await stream.WriteAsync(packet, connection.Token);
                    }
                }
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                Console.WriteLine("[DummyClient] 종료합니다.");
                return 0;
            }
            catch (OperationCanceledException)
            {
                if (receiveTask != null) { PrintReceiveEnd(await receiveTask); }
                else { Console.Error.WriteLine("[DummyClient] 로그인 요청 시간이 초과되었습니다."); }
                return 1;
            }
            catch (HttpRequestException error)
            {
                Console.Error.WriteLine(error.StatusCode.HasValue
                    ? $"[DummyClient] HTTP 로그인 거절: {(int)error.StatusCode.Value}."
                    : "[DummyClient] 로그인 서버에 연결할 수 없습니다.");
                return 1;
            }
            catch (SocketException)
            {
                Console.Error.WriteLine("[DummyClient] 게임 서버에 연결할 수 없습니다.");
                return 1;
            }
            catch (Exception error) when (error is JsonException or InvalidDataException)
            {
                Console.Error.WriteLine("[DummyClient] 로그인 서버의 응답 형식이 올바르지 않습니다.");
                return 1;
            }
            catch (Exception error) when (error is IOException or ObjectDisposedException)
            {
                Console.Error.WriteLine("[DummyClient] 서버 연결을 사용할 수 없습니다.");
                return 1;
            }
            finally
            {
                try
                {
                    connection?.Cancel();
                }
                finally
                {
                    client.Close();
                    if (receiveTask != null) { await receiveTask; }
                    connection?.Dispose();
                }
            }
        }

        private static async Task<string> LoginAsync(Uri uri, string name, CancellationToken cancellationToken)
        {
            using HttpClient http = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(10),
                MaxResponseContentBufferSize = 4096,
            };
            using HttpResponseMessage response = await http.PostAsJsonAsync(uri, new { name }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using JsonDocument result = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            if (result.RootElement.ValueKind != JsonValueKind.Object ||
                !result.RootElement.TryGetProperty("token", out JsonElement token) || token.ValueKind != JsonValueKind.String ||
                string.IsNullOrEmpty(token.GetString()))
            {
                throw new InvalidDataException("토큰 없는 로그인 응답");
            }
            return token.GetString()!;
        }

        private static byte[]? MakeRequest(string line)
        {
            if (line.StartsWith("/room ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && int.TryParse(parts[1], out int roomId) && roomId > 0)
                {
                    return new ChangeRoomReq { RoomId = roomId }.ToPacket();
                }
                Console.WriteLine("사용법: /room 번호   (예: /room 2)");
                return null;
            }
            if (line.StartsWith("/move ", StringComparison.Ordinal))
            {
                string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 3 && float.TryParse(parts[1], out float x) && float.TryParse(parts[2], out float y) &&
                    float.IsFinite(x) && float.IsFinite(y))
                {
                    return new MoveReq { X = x, Y = y }.ToPacket();
                }
                Console.WriteLine("사용법: /move x y   (예: /move 3.5 7)");
                return null;
            }
            return new ChatReq { Message = line }.ToPacket();
        }

        private static async Task<ReceiveResult> ReceiveAsync(NetworkStream stream, CancellationTokenSource connection)
        {
            byte[] readBuffer = new byte[4096];
            byte[] receiveBuffer = new byte[ushort.MaxValue];
            int receiveSize = 0;
            try
            {
                while (true)
                {
                    int received = await stream.ReadAsync(readBuffer, connection.Token);
                    if (received == 0) { return ReceiveResult.ServerClosed; }
                    if (PacketFramer.Process(receiveBuffer, ref receiveSize, readBuffer, received, OnPacket) != FrameResult.Success)
                    {
                        return ReceiveResult.ProtocolError;
                    }
                }
            }
            catch (OperationCanceledException) when (connection.IsCancellationRequested) { return ReceiveResult.Cancelled; }
            catch (InvalidDataException) { return ReceiveResult.ProtocolError; }
            catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException) { return ReceiveResult.IoError; }
            finally
            {
                connection.Cancel();
            }
        }

        private static void PrintReceiveEnd(ReceiveResult result)
        {
            Console.Error.WriteLine(result == ReceiveResult.ProtocolError
                ? "[DummyClient] 서버에서 잘못된 패킷을 받아 종료합니다."
                : "[DummyClient] 서버 연결이 끊겼습니다.");
        }

        private static T Require<T>(T? packet) where T : class => packet ?? throw new InvalidDataException("잘못된 서버 패킷");

        private static void OnPacket(PacketId id, byte[] buffer, int offset, int length)
        {
            switch (id)
            {
                case PacketId.Login:
                    Console.WriteLine($"[DummyClient] 로그인 응답: {Require(LoginRes.ToValue(buffer, offset, length)).Message}");
                    break;
                case PacketId.Echo:
                    Console.WriteLine($"[에코] {Require(EchoRes.ToValue(buffer, offset, length)).Message}");
                    break;
                case PacketId.Chat:
                    Console.WriteLine($"[채팅] {Require(ChatNtf.ToValue(buffer, offset, length)).Message}");
                    break;
                case PacketId.MoveNtf:
                {
                    MoveNtf packet = Require(MoveNtf.ToValue(buffer, offset, length));
                    Console.WriteLine($"[이동] {packet.Name} → ({packet.X}, {packet.Y})");
                    break;
                }
                case PacketId.PlayerEnterNtf:
                {
                    PlayerEnterNtf packet = Require(PlayerEnterNtf.ToValue(buffer, offset, length));
                    Console.WriteLine($"[입장] {packet.Name} → ({packet.X}, {packet.Y})");
                    break;
                }
                case PacketId.PlayerLeaveNtf:
                    Console.WriteLine($"[퇴장] {Require(PlayerLeaveNtf.ToValue(buffer, offset, length)).Name}");
                    break;
                case PacketId.ChangeRoomNtf:
                {
                    ChangeRoomNtf packet = Require(ChangeRoomNtf.ToValue(buffer, offset, length));
                    Console.WriteLine($"[방] {packet.RoomId} → ({packet.X}, {packet.Y})");
                    break;
                }
                default:
                    throw new InvalidDataException("허용하지 않는 서버 패킷");
            }
        }
    }
}
