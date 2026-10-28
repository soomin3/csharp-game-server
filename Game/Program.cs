using System.Text;
using Common.Auth;
using Common.Job;
using Common.Logging;
using Common.Network;
using Game.Configuration;
using Game.Diagnostics;
using Game.Network;
using Game.Persistence;
using Game.Room;

Console.OutputEncoding = new UTF8Encoding(false);
ServerOptions options;
try { options = ServerOptions.Parse(args); }
catch (ArgumentException error) { Console.Error.WriteLine(error.Message); return 2; }

FileLog? fileLog = null;
int exitCode = 1;
try
{
    fileLog = new FileLog(options.LogPath);
    exitCode = await RunServerAsync(options);
}
catch (Exception error)
{
    Console.Error.WriteLine($"[Game] 실행 실패: {error.GetType().Name}: {error.Message}");
}
finally { fileLog?.Dispose(); }
return fileLog?.HasFileError == true ? 1 : exitCode;

static async Task<int> RunServerAsync(ServerOptions options)
{
    TokenService tokens = TokenService.FromEnvironment();
    GameLog.Verbose = !options.Quiet;
    await using SqlitePlayerStore store = new SqlitePlayerStore(options.DataPath);
    await store.DrainAsync();
    await using RoomScheduler scheduler = new RoomScheduler(options.Workers);
    RoomManager manager = new RoomManager(scheduler, options.Rooms, store);
    TickMetrics metrics = new TickMetrics();
    foreach (GameRoom room in manager.Rooms) room.Metrics = metrics;
    GameHandler handler = new GameHandler(manager, tokens);
    SessionOptions sessionOptions = new SessionOptions { MaxPendingPackets = options.SendPackets, MaxPendingBytes = options.SendBytes };
    TServer server = new TServer(options.Port, client => new GameSession(client, handler, sessionOptions));
    using CancellationTokenSource accepting = new CancellationTokenSource();
    using CancellationTokenSource ticks = new CancellationTokenSource();
    if (options.ShutdownAfterSeconds > 0) accepting.CancelAfter(TimeSpan.FromSeconds(options.ShutdownAfterSeconds));
    Task[] tickTasks = manager.Rooms.Select(room => room.RunTickLoopAsync(ticks.Token)).ToArray();
    ConsoleCancelEventHandler cancelHandler = (_, input) => { input.Cancel = true; accepting.Cancel(); };
    Console.CancelKeyPress += cancelHandler;
    int failures = 0;
    try
    {
        Console.WriteLine($"[Game] 시작: 방 {options.Rooms}개, 워커 {options.Workers}개, 송신 한도 {options.SendPackets}개/{options.SendBytes}바이트");
        await server.RunAsync(accepting.Token);
    }
    catch (Exception error)
    {
        failures++;
        Console.Error.WriteLine($"[Game] 서버 오류: {error.GetType().Name}");
    }
    finally
    {
        Console.CancelKeyPress -= cancelHandler;
        server.StopAccepting();
        ticks.Cancel();
        await CleanupAsync("틱 종료", () => Task.WhenAll(tickTasks));
        await CleanupAsync("접속 종료", () => server.DisconnectAllAsync());
        manager.DisconnectAll();
        await CleanupAsync("퇴장 처리", () => scheduler.DrainAsync());
        await CleanupAsync("저장 작업", () => manager.DrainPersistenceAsync());
        await CleanupAsync("DB 처리", () => store.DrainAsync());
        await CleanupAsync("마지막 방 일감", () => scheduler.DrainAsync());
        await CleanupAsync("워커 종료", () => scheduler.CompleteAsync());
        await CleanupAsync("DB 종료", () => store.DisposeAsync().AsTask());
        if (server.SessionFailureCount != 0) failures++;
        long jobErrors = manager.Rooms.Sum(room => room.FailedJobCount);
        if (jobErrors != 0) failures++;
        if (options.MetricsPath != null)
        {
            await CleanupAsync("측정 결과 기록", async () =>
            {
                var report = new
                {
                    capturedUtc = DateTimeOffset.UtcNow,
                    metrics = metrics.Snapshot(),
                    traffic = new { packetsSent = TSession.TotalPacketsSent, bytesSent = TSession.TotalBytesSent },
                    activeSessions = server.ActiveSessionCount,
                    pendingJobs = scheduler.PendingJobCount,
                    pendingPersistence = manager.PendingPersistenceCount,
                    reservedNames = manager.ReservedNameCount,
                    roomPlayers = manager.Rooms.Sum(room => room.SessionCount),
                    sessionErrors = server.SessionFailureCount, jobErrors, shutdownErrors = failures,
                    environment = new
                    {
                        framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
                        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                        processorCount = Environment.ProcessorCount,
                        assemblyMvid = typeof(GameRoom).Module.ModuleVersionId.ToString()
                    }
                };
                Directory.CreateDirectory(Path.GetDirectoryName(options.MetricsPath)!);
                string json = System.Text.Json.JsonSerializer.Serialize(report, new System.Text.Json.JsonSerializerOptions
                {
                    WriteIndented = true, PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });
                await File.WriteAllTextAsync(options.MetricsPath, json);
            });
        }
        Console.WriteLine($"[Game] 종료 완료: 저장·일감 정리, 남은 접속 {server.ActiveSessionCount}개, 오류 {failures}개");
    }
    return failures == 0 ? 0 : 1;

    async Task CleanupAsync(string name, Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception error)
        {
            failures++;
            Console.Error.WriteLine($"[Game] {name} 실패: {error.GetType().Name}");
        }
    }
}
