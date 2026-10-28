using System.Text.Json;
using LoadClient;

LoadOptions options;
try { options = LoadOptions.Parse(args); }
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }

string runId = Guid.NewGuid().ToString("N")[..8];
LoadPeer[] peers = Enumerable.Range(0, options.Clients).Select(index => new LoadPeer(runId, index, options)).ToArray();
using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
using CancellationTokenSource setupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
string phase = "setup";
try
{
    await WriteResultAsync(options.Output, new { success = false, phase, requestedClients = options.Clients, errorCount = 0 });
    await Parallel.ForEachAsync(peers, new ParallelOptions { MaxDegreeOfParallelism = 32, CancellationToken = setupTimeout.Token },
        async (peer, token) => await peer.ConnectAsync(http, options, token));
    Console.WriteLine($"[LoadClient] Setup: {peers.Count(peer => peer.IsHealthy)}/{options.Clients}");

    using CancellationTokenSource movement = new CancellationTokenSource();
    Task[] tasks = peers.Where(peer => peer.IsHealthy).Select(peer => peer.MoveAsync(options.IntervalMs, movement.Token)).ToArray();
    await Task.Delay(TimeSpan.FromSeconds(options.WarmupSeconds));
    DateTimeOffset measurementStart = DateTimeOffset.UtcNow;
    int healthyAtStart = peers.Count(peer => peer.IsHealthy);
    PeerCounters[] beforePeers = peers.Select(peer => peer.Counters).ToArray();
    PeerCounters before = Sum(beforePeers);
    phase = "measurement";
    await Task.Delay(TimeSpan.FromSeconds(options.DurationSeconds));
    PeerCounters[] afterPeers = peers.Select(peer => peer.Counters).ToArray();
    PeerCounters after = Sum(afterPeers);
    int healthyAtEnd = peers.Count(peer => peer.IsHealthy);
    DateTimeOffset measurementEnd = DateTimeOffset.UtcNow;
    movement.Cancel();
    await Task.WhenAll(tasks);

    var failures = peers.Where(peer => peer.Failure != null).Select(peer => new { peer.Name, peer.Failure }).ToArray();
    int clientsWithMeasuredMoves = afterPeers.Zip(beforePeers).Count(pair => pair.First.MovesSent > pair.Second.MovesSent);
    int clientsWithConfirmedMoves = afterPeers.Zip(beforePeers).Count(pair => pair.First.MovesConfirmed > pair.Second.MovesConfirmed);
    bool success = healthyAtStart == options.Clients && healthyAtEnd == options.Clients &&
        clientsWithMeasuredMoves == options.Clients && clientsWithConfirmedMoves == options.Clients && failures.Length == 0;
    var result = new
    {
        success, requestedClients = options.Clients, healthyAtStart, healthyAtEnd, clientsWithMeasuredMoves, clientsWithConfirmedMoves, errorCount = failures.Length,
        measurementStartUtc = measurementStart, measurementEndUtc = measurementEnd,
        durationSeconds = (measurementEnd - measurementStart).TotalSeconds,
        options.IntervalMs, options.Rooms, options.Seed,
        packetsReceived = after.PacketsReceived - before.PacketsReceived,
        bytesReceived = after.BytesReceived - before.BytesReceived,
        packetsSent = after.PacketsSent - before.PacketsSent,
        movesSent = after.MovesSent - before.MovesSent,
        movesConfirmed = after.MovesConfirmed - before.MovesConfirmed,
        failures = failures.Take(20).ToArray()
    };
    string json = await WriteResultAsync(options.Output, result);
    Console.WriteLine(json);
    return success ? 0 : 1;
}
catch (Exception error)
{
    Console.Error.WriteLine("[LoadClient] " + error.GetType().Name + ": " + error.Message);
    try
    {
        await WriteResultAsync(options.Output, new
        {
            success = false, phase, requestedClients = options.Clients,
            errorCount = 1 + peers.Count(peer => peer.Failure != null), error = error.GetType().Name
        });
    }
    catch (Exception writeError) { Console.Error.WriteLine("Result write failed: " + writeError.GetType().Name); }
    return 1;
}
finally
{
    foreach (LoadPeer peer in peers) await peer.DisposeAsync();
}

static PeerCounters Sum(PeerCounters[] values)
{
    return new PeerCounters(values.Sum(value => value.PacketsReceived), values.Sum(value => value.BytesReceived),
        values.Sum(value => value.PacketsSent), values.Sum(value => value.MovesSent), values.Sum(value => value.MovesConfirmed));
}

static async Task<string> WriteResultAsync(string path, object result)
{
    string json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    await File.WriteAllTextAsync(path, json);
    return json;
}
