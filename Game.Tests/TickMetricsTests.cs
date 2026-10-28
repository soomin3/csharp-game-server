using System.Text.Json;
using Game.Diagnostics;

namespace Game.Tests;

public class TickMetricsTests
{
    [Fact]
    public void 빈_측정은_유한한_0과_빈_샘플을_반환한다()
    {
        TickMetricsSnapshot snapshot = new TickMetrics().Snapshot();
        Assert.Equal(0, snapshot.TotalRecorded);
        Assert.Equal(0, snapshot.RetainedCount);
        Assert.Equal(0, snapshot.DroppedCount);
        Assert.Equal(new TickStatistics(0, 0, 0, 0, 0, 0, 0, 0), snapshot.Overall);
        Assert.Empty(snapshot.Rooms);
        Assert.Empty(snapshot.Samples);
        Assert.NotEmpty(JsonSerializer.Serialize(snapshot));
    }

    [Fact]
    public void 알려진_분포의_평균과_nearest_rank_백분위를_계산한다()
    {
        TickMetrics metrics = new TickMetrics();
        for (int i = 1; i <= 100; i++)
        {
            metrics.Record(i % 2, i, i, i, i % 7);
        }

        TickMetricsSnapshot snapshot = metrics.Snapshot();

        Assert.Equal(100, snapshot.Overall.Count);
        Assert.Equal(100, snapshot.Overall.MaxMilliseconds);
        Assert.Equal(50.5, snapshot.Overall.MeanMilliseconds, precision: 10);
        Assert.Equal(95, snapshot.Overall.P95Milliseconds);
        Assert.Equal(99, snapshot.Overall.P99Milliseconds);
        Assert.Equal(50, snapshot.Overall.Over50MillisecondsCount);
        Assert.Equal(6, snapshot.Overall.MaxPendingJobs);
        Assert.Equal(100, snapshot.Overall.MaxPlayerCount);
        Assert.Equal(new[] { 0, 1 }, snapshot.Rooms.Select(room => room.RoomId));
        Assert.Equal(50, snapshot.Rooms[0].Statistics.Count);
        Assert.Equal(96, snapshot.Rooms[0].Statistics.P95Milliseconds);
        Assert.Equal(100, snapshot.Rooms[0].Statistics.P99Milliseconds);
        Assert.Equal(95, snapshot.Rooms[1].Statistics.P95Milliseconds);
        Assert.Equal(99, snapshot.Rooms[1].Statistics.P99Milliseconds);
    }

    [Fact]
    public void 측정_구간은_시작을_포함하고_끝을_제외한다()
    {
        TickMetrics metrics = new TickMetrics();
        metrics.Record(0, 100, 1, 10, 100);
        metrics.Record(1, 200, 50, 20, 7);
        metrics.Record(0, 300, 51, 30, 9);
        metrics.Record(1, 400, 2, 40, 200);

        TickMetricsSnapshot snapshot = metrics.Snapshot(200, 400);

        Assert.Equal(4, snapshot.TotalRecorded);
        Assert.Equal(4, snapshot.RetainedCount);
        Assert.Equal(2, snapshot.Overall.Count);
        Assert.Equal(new long[] { 200, 300 }, snapshot.Samples.Select(sample => sample.TimestampUtcMilliseconds));
        Assert.Equal(50.5, snapshot.Overall.MeanMilliseconds);
        Assert.Equal(1, snapshot.Overall.Over50MillisecondsCount);
        Assert.Equal(9, snapshot.Overall.MaxPendingJobs);
        Assert.Equal(30, snapshot.Overall.MaxPlayerCount);
        Assert.All(snapshot.Rooms, room => Assert.Equal(1, room.Statistics.Count));
        Assert.Equal(1, metrics.Snapshot(endUtcMilliseconds: 200).Overall.Count);
        Assert.Equal(1, metrics.Snapshot(startUtcMilliseconds: 400).Overall.Count);
        Assert.Equal(0, metrics.Snapshot(300, 300).Overall.Count);
    }

    [Fact]
    public void 용량을_넘으면_기록순으로_오래된_샘플을_버린다()
    {
        TickMetrics metrics = new TickMetrics(capacity: 3);
        foreach (long time in new long[] { 100, 200, 300, 50 })
        {
            metrics.Record(0, time, time, 1, 0);
        }

        TickMetricsSnapshot snapshot = metrics.Snapshot();

        Assert.Equal(3, snapshot.Capacity);
        Assert.Equal(4, snapshot.TotalRecorded);
        Assert.Equal(3, snapshot.RetainedCount);
        Assert.Equal(1, snapshot.DroppedCount);
        Assert.Equal(new long[] { 200, 300, 50 }, snapshot.Samples.Select(sample => sample.TimestampUtcMilliseconds));
        Assert.Equal(50, Assert.Single(metrics.Snapshot(0, 100).Samples).TimestampUtcMilliseconds);
    }

    [Fact]
    public void 반환한_샘플을_수정해도_원본_보관소는_바뀌지_않는다()
    {
        TickMetrics metrics = new TickMetrics();
        metrics.Record(0, 100, 1, 1, 0);
        TickMetricsSnapshot old = metrics.Snapshot();
        old.Samples[0] = new TickSample(0, 100, 999, 999, 999);
        metrics.Record(0, 200, 2, 2, 0);

        TickMetricsSnapshot current = metrics.Snapshot();

        Assert.Equal(2, current.Overall.Count);
        Assert.Equal(1, current.Samples[0].ElapsedMilliseconds);
        Assert.Equal(2, current.Overall.MaxPlayerCount);
        Assert.Single(old.Samples);
    }

    [Fact]
    public async Task 여러_방의_동시기록과_스냅샷은_일관된_보관수를_유지한다()
    {
        TickMetrics metrics = new TickMetrics(capacity: 512);
        Task[] writers = Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
        {
            for (int i = 0; i < 1000; i++)
            {
                metrics.Record(worker % 4, worker * 1000L + i, i % 70, worker, i % 11);
            }
        })).ToArray();
        Task writersDone = Task.WhenAll(writers);
        Task reader = Task.Run(async () =>
        {
            do
            {
                TickMetricsSnapshot snapshot = metrics.Snapshot();
                Assert.Equal(snapshot.RetainedCount, snapshot.Samples.Length);
                Assert.Equal(snapshot.Overall.Count, snapshot.Samples.Length);
                Assert.Equal(snapshot.TotalRecorded - snapshot.RetainedCount, snapshot.DroppedCount);
                Assert.InRange(snapshot.RetainedCount, 0, 512);
                Assert.Equal(snapshot.Overall.Count, snapshot.Rooms.Sum(room => room.Statistics.Count));
                await Task.Yield();
            } while (!writersDone.IsCompleted);
        });

        await writersDone.WaitAsync(TimeSpan.FromSeconds(10));
        await reader.WaitAsync(TimeSpan.FromSeconds(10));

        TickMetricsSnapshot final = metrics.Snapshot();
        Assert.Equal(8000, final.TotalRecorded);
        Assert.Equal(512, final.RetainedCount);
        Assert.Equal(7488, final.DroppedCount);
    }

    [Fact]
    public void JSON_샘플을_다시_읽어_같은_측정구간을_분석할_수_있다()
    {
        TickMetrics metrics = new TickMetrics();
        metrics.Record(1, 100, 10, 20, 2);
        metrics.Record(2, 200, 20, 30, 3);
        string json = JsonSerializer.Serialize(metrics.Snapshot());
        TickMetricsSnapshot loaded = JsonSerializer.Deserialize<TickMetricsSnapshot>(json)!;

        TickWindowSummary summary = TickMetrics.Summarize(loaded.Samples, 150, 300);

        Assert.Equal(1, summary.Overall.Count);
        Assert.Equal(20, summary.Overall.MaxMilliseconds);
        Assert.Equal(2, Assert.Single(summary.Rooms).RoomId);
        Assert.Equal(200, Assert.Single(summary.Samples).TimestampUtcMilliseconds);
    }

    [Fact]
    public void 유한한_큰_값들의_평균도_무한대로_넘치지_않는다()
    {
        TickMetrics metrics = new TickMetrics();
        metrics.Record(0, 0, double.MaxValue, 0, 0);
        metrics.Record(0, 1, double.MaxValue, 0, 0);
        TickMetricsSnapshot snapshot = metrics.Snapshot();
        Assert.Equal(double.MaxValue, snapshot.Overall.MeanMilliseconds);
        Assert.NotEmpty(JsonSerializer.Serialize(snapshot));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void 잘못된_경과시간은_기록하지_않는다(double elapsed)
    {
        TickMetrics metrics = new TickMetrics();
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.Record(0, 0, elapsed, 0, 0));
        Assert.Equal(0, metrics.Snapshot().TotalRecorded);
    }

    [Theory]
    [InlineData(-1, 0, 0, 0)]
    [InlineData(0, -1, 0, 0)]
    [InlineData(0, 0, -1, 0)]
    [InlineData(0, 0, 0, -1)]
    public void 음수_방번호_시각_인원_대기량을_거절한다(int roomId, long time, int players, int pending)
    {
        TickMetrics metrics = new TickMetrics();
        Assert.Throws<ArgumentOutOfRangeException>(() => metrics.Record(roomId, time, 1, players, pending));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 보관_용량은_양수여야_한다(int capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickMetrics(capacity));
    }

    [Theory]
    [InlineData(-1, 10)]
    [InlineData(0, -1)]
    [InlineData(20, 10)]
    public void 잘못된_측정구간은_스냅샷과_후처리에서_거절한다(long start, long end)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickMetrics().Snapshot(start, end));
        Assert.Throws<ArgumentOutOfRangeException>(() => TickMetrics.Summarize(Array.Empty<TickSample>(), start, end));
    }

    [Fact]
    public void JSON에서_읽은_잘못된_샘플도_후처리에서_거절한다()
    {
        TickSample[] samples = { new TickSample(0, 0, double.NaN, 0, 0) };
        Assert.Throws<ArgumentOutOfRangeException>(() => TickMetrics.Summarize(samples));
    }
}
