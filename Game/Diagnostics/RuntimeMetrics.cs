using System.Diagnostics;
using Common.Network;

namespace Game.Diagnostics;

// 누적값 표본이다. 측정 구간의 첫 표본과 마지막 표본의 차이로 할당량·GC·CPU·송신량을 계산한다.
public readonly record struct RuntimeSample(
    long TimestampUtcMilliseconds,
    long AllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double CpuMilliseconds,
    long PacketsSent,
    long BytesSent,
    long SocketWrites);

public sealed class RuntimeMetrics : IAsyncDisposable
{
    public const int DefaultCapacity = 100_000;
    private readonly object _gate = new object();
    private readonly List<RuntimeSample> _samples = new List<RuntimeSample>();
    private readonly int _capacity;
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
    private readonly Task _sampling;
    private long _droppedCount;

    public RuntimeMetrics(TimeSpan interval, int capacity = DefaultCapacity)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
        Record();
        _sampling = SampleAsync(interval, _stopping.Token);
    }

    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    public RuntimeSample Capture() => new RuntimeSample(
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        GC.GetTotalAllocatedBytes(false),
        GC.CollectionCount(0),
        GC.CollectionCount(1),
        GC.CollectionCount(2),
        _process.TotalProcessorTime.TotalMilliseconds,
        TSession.TotalPacketsSent,
        TSession.TotalBytesSent,
        TSession.TotalSocketWrites);

    public RuntimeSample[] Snapshot()
    {
        lock (_gate) { return _samples.ToArray(); }
    }

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        await _sampling;
        // 종료 직전 값을 남겨 마지막 측정 구간도 닫히게 한다.
        Record();
        _stopping.Dispose();
        _process.Dispose();
    }

    private async Task SampleAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                Record();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private void Record()
    {
        RuntimeSample sample = Capture();
        lock (_gate)
        {
            if (_samples.Count >= _capacity)
            {
                _droppedCount++;
                return;
            }
            _samples.Add(sample);
        }
    }
}
