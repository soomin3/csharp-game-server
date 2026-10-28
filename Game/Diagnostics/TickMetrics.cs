namespace Game.Diagnostics;

public readonly record struct TickSample(
    int RoomId,
    long TimestampUtcMilliseconds,
    double ElapsedMilliseconds,
    int PlayerCount,
    int PendingJobs);

public sealed record TickStatistics(
    int Count,
    double MaxMilliseconds,
    double MeanMilliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    int Over50MillisecondsCount,
    int MaxPendingJobs,
    int MaxPlayerCount);

public sealed record RoomTickStatistics(int RoomId, TickStatistics Statistics);

public sealed record TickWindowSummary(
    long? WindowStartUtcMilliseconds,
    long? WindowEndUtcMilliseconds,
    TickStatistics Overall,
    RoomTickStatistics[] Rooms,
    TickSample[] Samples);

public sealed record TickMetricsSnapshot(
    int Capacity,
    long TotalRecorded,
    int RetainedCount,
    long DroppedCount,
    long? WindowStartUtcMilliseconds,
    long? WindowEndUtcMilliseconds,
    TickStatistics Overall,
    RoomTickStatistics[] Rooms,
    TickSample[] Samples);

public sealed class TickMetrics
{
    public const int DefaultCapacity = 100_000;
    private readonly object _gate = new object();
    private readonly TickSample[] _buffer;
    private int _next;
    private int _count;
    private long _totalRecorded;

    public TickMetrics(int capacity = DefaultCapacity)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }
        _buffer = new TickSample[capacity];
    }

    public void Record(int roomId, long timestampUtcMilliseconds, double elapsedMilliseconds, int playerCount, int pendingJobs)
    {
        Record(new TickSample(roomId, timestampUtcMilliseconds, elapsedMilliseconds, playerCount, pendingJobs));
    }

    public void Record(TickSample sample)
    {
        Validate(sample);
        lock (_gate)
        {
            _buffer[_next] = sample;
            _next++;
            if (_next == _buffer.Length)
            {
                _next = 0;
            }
            if (_count < _buffer.Length)
            {
                _count++;
            }
            _totalRecorded++;
        }
    }

    // Retention follows recording order. The query window is [start, end).
    public TickMetricsSnapshot Snapshot(long? startUtcMilliseconds = null, long? endUtcMilliseconds = null)
    {
        ValidateWindow(startUtcMilliseconds, endUtcMilliseconds);
        TickSample[] retained;
        long totalRecorded;
        lock (_gate)
        {
            retained = new TickSample[_count];
            int first = _count == _buffer.Length ? _next : 0;
            int firstLength = Math.Min(_count, _buffer.Length - first);
            Array.Copy(_buffer, first, retained, 0, firstLength);
            if (firstLength < _count)
            {
                Array.Copy(_buffer, 0, retained, firstLength, _count - firstLength);
            }
            totalRecorded = _totalRecorded;
        }

        TickWindowSummary summary = SummarizeCore(retained, startUtcMilliseconds, endUtcMilliseconds);
        return new TickMetricsSnapshot(_buffer.Length, totalRecorded, retained.Length, totalRecorded - retained.Length,
            summary.WindowStartUtcMilliseconds, summary.WindowEndUtcMilliseconds, summary.Overall, summary.Rooms, summary.Samples);
    }

    // This also supports analysis of timestamped samples read back from JSON.
    public static TickWindowSummary Summarize(IEnumerable<TickSample> samples, long? startUtcMilliseconds = null, long? endUtcMilliseconds = null)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ValidateWindow(startUtcMilliseconds, endUtcMilliseconds);
        return SummarizeCore(samples.ToArray(), startUtcMilliseconds, endUtcMilliseconds);
    }

    private static TickWindowSummary SummarizeCore(TickSample[] samples, long? start, long? end)
    {
        List<TickSample> selected = new List<TickSample>(samples.Length);
        foreach (TickSample sample in samples)
        {
            Validate(sample);
            if ((!start.HasValue || sample.TimestampUtcMilliseconds >= start.Value) &&
                (!end.HasValue || sample.TimestampUtcMilliseconds < end.Value))
            {
                selected.Add(sample);
            }
        }

        TickSample[] filtered = selected.ToArray();
        RoomTickStatistics[] rooms = filtered.GroupBy(sample => sample.RoomId).OrderBy(group => group.Key)
            .Select(group => new RoomTickStatistics(group.Key, Calculate(group.ToArray()))).ToArray();
        return new TickWindowSummary(start, end, Calculate(filtered), rooms, filtered);
    }

    private static TickStatistics Calculate(TickSample[] samples)
    {
        if (samples.Length == 0)
        {
            return new TickStatistics(0, 0, 0, 0, 0, 0, 0, 0);
        }

        double[] elapsed = new double[samples.Length];
        double mean = 0;
        int over50 = 0;
        int maxPending = 0;
        int maxPlayers = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            TickSample sample = samples[i];
            elapsed[i] = sample.ElapsedMilliseconds;
            // An incremental mean avoids overflowing a sum of finite values.
            mean += (sample.ElapsedMilliseconds - mean) / (i + 1);
            if (sample.ElapsedMilliseconds > 50)
            {
                over50++;
            }
            maxPending = Math.Max(maxPending, sample.PendingJobs);
            maxPlayers = Math.Max(maxPlayers, sample.PlayerCount);
        }

        Array.Sort(elapsed);
        return new TickStatistics(samples.Length, elapsed[^1], mean,
            NearestRank(elapsed, 0.95), NearestRank(elapsed, 0.99), over50, maxPending, maxPlayers);
    }

    private static double NearestRank(double[] sorted, double percentile) =>
        sorted[(int)Math.Ceiling(sorted.Length * percentile) - 1];

    private static void Validate(TickSample sample)
    {
        if (sample.RoomId < 0) throw new ArgumentOutOfRangeException(nameof(sample.RoomId));
        if (sample.TimestampUtcMilliseconds < 0) throw new ArgumentOutOfRangeException(nameof(sample.TimestampUtcMilliseconds));
        if (!double.IsFinite(sample.ElapsedMilliseconds) || sample.ElapsedMilliseconds < 0)
            throw new ArgumentOutOfRangeException(nameof(sample.ElapsedMilliseconds));
        if (sample.PlayerCount < 0) throw new ArgumentOutOfRangeException(nameof(sample.PlayerCount));
        if (sample.PendingJobs < 0) throw new ArgumentOutOfRangeException(nameof(sample.PendingJobs));
    }

    private static void ValidateWindow(long? start, long? end)
    {
        if (start < 0) throw new ArgumentOutOfRangeException(nameof(start));
        if (end < 0) throw new ArgumentOutOfRangeException(nameof(end));
        if (start.HasValue && end.HasValue && start.Value > end.Value)
            throw new ArgumentOutOfRangeException(nameof(end), "The window end must not precede its start.");
    }
}
