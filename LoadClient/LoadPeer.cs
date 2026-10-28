using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;
using Common.Network;

namespace LoadClient;

internal sealed class LoadPeer : IAsyncDisposable
{
    private readonly TcpClient _client = new TcpClient { NoDelay = true };
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
    private readonly TaskCompletionSource<bool> _loginReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource<bool> _roomReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Random _random;
    private readonly int _targetRoom;
    private NetworkStream? _stream;
    private Task _receiveTask = Task.CompletedTask;
    private string? _failure;
    private int _ended;
    private int _loggedIn;
    private long _packetsReceived, _bytesReceived, _packetsSent, _movesSent, _movesConfirmed;
    private float _x, _y;
    private float _confirmedX, _confirmedY;
    private bool _hasConfirmedPosition;

    public string Name { get; }
    public string? Failure => Volatile.Read(ref _failure);
    public bool IsHealthy => Volatile.Read(ref _loggedIn) != 0 && Volatile.Read(ref _ended) == 0 && Failure == null;
    public PeerCounters Counters => new PeerCounters(Interlocked.Read(ref _packetsReceived), Interlocked.Read(ref _bytesReceived),
        Interlocked.Read(ref _packetsSent), Interlocked.Read(ref _movesSent), Interlocked.Read(ref _movesConfirmed));

    public LoadPeer(string runId, int index, LoadOptions options)
    {
        Name = $"load-{runId}-{index}";
        _targetRoom = 1 + index % options.Rooms;
        _random = new Random(unchecked(options.Seed + index * 7919));
        _x = _random.NextSingle() * 100;
        _y = _random.NextSingle() * 100;
    }

    public async Task ConnectAsync(HttpClient http, LoadOptions options, CancellationToken cancellationToken)
    {
        try
        {
            using HttpResponseMessage response = await http.PostAsJsonAsync(options.LoginUrl, new { name = Name }, cancellationToken);
            response.EnsureSuccessStatusCode();
            using JsonDocument json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            string token = json.RootElement.GetProperty("token").GetString() ?? throw new InvalidDataException("Missing login token.");
            await _client.ConnectAsync(options.Host, options.Port, cancellationToken);
            _stream = _client.GetStream();
            _receiveTask = ReceiveAsync();
            await SendAsync(new LoginReq { Token = token }.ToPacket(), cancellationToken);
            await _loginReady.Task.WaitAsync(cancellationToken);
            if (_targetRoom != 1)
            {
                await SendAsync(new ChangeRoomReq { RoomId = _targetRoom }.ToPacket(), cancellationToken);
                await _roomReady.Task.WaitAsync(cancellationToken);
            }
            await SendMoveAsync(cancellationToken);
        }
        catch (Exception error)
        {
            Fail("setup: " + error.GetType().Name);
        }
    }

    public async Task MoveAsync(int intervalMs, CancellationToken cancellationToken)
    {
        try
        {
            using PeriodicTimer timer = new PeriodicTimer(TimeSpan.FromMilliseconds(intervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken) && IsHealthy)
            {
                _x = Math.Clamp(_x + (_random.NextSingle() * 2 - 1) * 3, 0, 100);
                _y = Math.Clamp(_y + (_random.NextSingle() * 2 - 1) * 3, 0, 100);
                await SendMoveAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception error) { Fail("move: " + error.GetType().Name); }
    }

    private async Task SendMoveAsync(CancellationToken cancellationToken)
    {
        await SendAsync(new MoveReq { X = _x, Y = _y }.ToPacket(), cancellationToken);
        Interlocked.Increment(ref _movesSent);
    }

    private async Task SendAsync(byte[] packet, CancellationToken cancellationToken)
    {
        await _stream!.WriteAsync(packet, cancellationToken);
        Interlocked.Increment(ref _packetsSent);
    }

    private async Task ReceiveAsync()
    {
        byte[] readBuffer = new byte[8192];
        byte[] receiveBuffer = new byte[ushort.MaxValue];
        int receiveSize = 0;
        try
        {
            while (true)
            {
                int count = await _stream!.ReadAsync(readBuffer, _stopping.Token);
                if (count == 0) break;
                Interlocked.Add(ref _bytesReceived, count);
                FrameResult result = PacketFramer.Process(receiveBuffer, ref receiveSize, readBuffer, count, OnPacket);
                if (result != FrameResult.Success) throw new InvalidDataException("Invalid server frame.");
            }
            if (!_stopping.IsCancellationRequested) Fail("server disconnected");
        }
        catch (Exception error) when (_stopping.IsCancellationRequested) { _ = error; }
        catch (Exception error) { Fail("receive: " + error.GetType().Name); }
        finally
        {
            Interlocked.Exchange(ref _ended, 1);
            _loginReady.TrySetCanceled();
            _roomReady.TrySetCanceled();
        }
    }

    private void OnPacket(PacketId id, byte[] buffer, int offset, int length)
    {
        Interlocked.Increment(ref _packetsReceived);
        bool valid;
        switch (id)
        {
            case PacketId.Login:
                valid = LoginRes.ToValue(buffer, offset, length) != null;
                if (valid) { Interlocked.Exchange(ref _loggedIn, 1); _loginReady.TrySetResult(true); }
                break;
            case PacketId.ChangeRoomNtf:
                ChangeRoomNtf? room = ChangeRoomNtf.ToValue(buffer, offset, length);
                valid = room != null;
                if (room?.RoomId == _targetRoom) _roomReady.TrySetResult(true);
                break;
            case PacketId.Echo: valid = EchoRes.ToValue(buffer, offset, length) != null; break;
            case PacketId.Chat: valid = ChatNtf.ToValue(buffer, offset, length) != null; break;
            case PacketId.MoveNtf:
                MoveNtf? move = MoveNtf.ToValue(buffer, offset, length);
                valid = move != null;
                if (move != null && string.Equals(move.Name, Name, StringComparison.Ordinal) &&
                    (!_hasConfirmedPosition || move.X != _confirmedX || move.Y != _confirmedY))
                {
                    _hasConfirmedPosition = true;
                    _confirmedX = move.X;
                    _confirmedY = move.Y;
                    Interlocked.Increment(ref _movesConfirmed);
                }
                break;
            case PacketId.PlayerEnterNtf: valid = PlayerEnterNtf.ToValue(buffer, offset, length) != null; break;
            case PacketId.PlayerLeaveNtf: valid = PlayerLeaveNtf.ToValue(buffer, offset, length) != null; break;
            default: valid = false; break;
        }
        if (!valid) throw new InvalidDataException("Malformed or unexpected server packet.");
    }

    private void Fail(string message) => Interlocked.CompareExchange(ref _failure, message, null);

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        _client.Close();
        await _receiveTask;
        _stopping.Dispose();
    }
}

internal readonly record struct PeerCounters(long PacketsReceived, long BytesReceived, long PacketsSent, long MovesSent, long MovesConfirmed);
