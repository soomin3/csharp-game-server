using Common.Job;

namespace Game.Tests
{
    public class RoomSchedulerTests
    {
        [Fact]
        public async Task 여러_생산자가_넣어도_같은_방은_한_번에_하나씩_빠짐없이_실행한다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(4, 7);
            JobSerializer room = new JobSerializer(scheduler);
            int running = 0;
            int overlaps = 0;
            int completed = 0;

            await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    room.Push(() =>
                    {
                        if (Interlocked.Increment(ref running) != 1) { Interlocked.Increment(ref overlaps); }
                        Thread.SpinWait(50);
                        Interlocked.Increment(ref completed);
                        Interlocked.Decrement(ref running);
                    });
                }
            })));
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(8000, completed);
            Assert.Equal(0, overlaps);
            Assert.Equal(0, running);
        }

        [Fact]
        public async Task 다른_방은_서로_다른_워커에서_동시에_실행한다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(2);
            JobSerializer first = new JobSerializer(scheduler);
            JobSerializer second = new JobSerializer(scheduler);
            using CountdownEvent started = new CountdownEvent(2);
            using ManualResetEventSlim release = new ManualResetEventSlim();
            int firstThread = 0;
            int secondThread = 0;
            first.Push(() => { firstThread = Environment.CurrentManagedThreadId; started.Signal(); release.Wait(TimeSpan.FromSeconds(10)); });
            second.Push(() => { secondThread = Environment.CurrentManagedThreadId; started.Signal(); release.Wait(TimeSpan.FromSeconds(10)); });
            try { Assert.True(started.Wait(TimeSpan.FromSeconds(5))); }
            finally { release.Set(); }
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotEqual(firstThread, secondThread);
        }

        [Fact]
        public async Task 작은_batch가_다른_방에_차례를_넘긴다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(1, 2);
            JobSerializer blocker = new JobSerializer(scheduler);
            JobSerializer hot = new JobSerializer(scheduler);
            JobSerializer cold = new JobSerializer(scheduler);
            using ManualResetEventSlim started = new ManualResetEventSlim();
            using ManualResetEventSlim release = new ManualResetEventSlim();
            List<string> order = new List<string>();
            blocker.Push(() => { started.Set(); release.Wait(TimeSpan.FromSeconds(10)); });
            Assert.True(started.Wait(TimeSpan.FromSeconds(5)));
            for (int i = 0; i < 10; i++) { hot.Push(() => order.Add("hot")); }
            cold.Push(() => order.Add("cold"));
            release.Set();
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, order.IndexOf("cold"));
            Assert.Equal(11, order.Count);
        }

        [Fact]
        public async Task Drain은_실행_중_추가한_후속_일감까지_기다리고_예외_후에도_계속한다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(2, 1);
            JobSerializer first = new JobSerializer(scheduler);
            JobSerializer second = new JobSerializer(scheduler);
            int completed = 0;
            first.Push(() => second.Push(() => Interlocked.Increment(ref completed)));
            first.Push(() => throw new InvalidOperationException("expected"));
            first.Push(() => Interlocked.Increment(ref completed));
            await scheduler.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(2, completed);
            await scheduler.CompleteAsync();
            Assert.Throws<ObjectDisposedException>(() => first.Push(() => { }));
        }

        [Fact]
        public async Task 빈_방을_반복해_깨워도_Barrier가_멈추지_않는다()
        {
            await using RoomScheduler scheduler = new RoomScheduler(4, 1);
            JobSerializer room = new JobSerializer(scheduler);
            int completed = 0;
            for (int round = 0; round < 500; round++)
            {
                room.Push(() => completed++);
                await room.BarrierAsync().WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(round + 1, completed);
            }
        }
    }
}
