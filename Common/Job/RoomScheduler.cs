using System.Threading.Channels;

namespace Common.Job
{
    public sealed class RoomScheduler : IAsyncDisposable
    {
        private readonly Channel<JobSerializer> _ready = Channel.CreateUnbounded<JobSerializer>();
        private readonly object _gate = new object();
        private readonly Task[] _workers;
        private TaskCompletionSource _idle = CompletedSource();
        private int _pendingJobs;
        private bool _accepting = true;

        public RoomScheduler(int workerCount = 2, int batchSize = 32)
        {
            if (workerCount <= 0 || batchSize <= 0)
            {
                throw new ArgumentOutOfRangeException();
            }

            BatchSize = batchSize;
            _workers = Enumerable.Range(0, workerCount).Select(_ => Task.Run(WorkerAsync)).ToArray();
        }

        public int BatchSize { get; }
        public int WorkerCount => _workers.Length;
        public int PendingJobCount { get { lock (_gate) { return _pendingJobs; } } }

        internal void JobQueued()
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(!_accepting, this);
                if (_pendingJobs++ == 0)
                {
                    _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                }
            }
        }

        internal void JobCompleted()
        {
            lock (_gate)
            {
                if (--_pendingJobs == 0)
                {
                    _idle.TrySetResult();
                }
            }
        }

        internal void Schedule(JobSerializer room)
        {
            if (!_ready.Writer.TryWrite(room))
            {
                throw new InvalidOperationException("종료된 스케줄러에 방을 예약했습니다.");
            }
        }

        private async Task WorkerAsync()
        {
            await foreach (JobSerializer room in _ready.Reader.ReadAllAsync())
            {
                room.RunScheduledBatch();
            }
        }

        // 호출 전에 네트워크와 타이머 생산자를 멈춘다. 실행 중 일감이 넣는 후속 일감도 센다.
        public Task DrainAsync(CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return _idle.Task.WaitAsync(cancellationToken);
            }
        }

        public async Task CompleteAsync()
        {
            while (true)
            {
                await DrainAsync();
                lock (_gate)
                {
                    if (_pendingJobs != 0)
                    {
                        continue;
                    }

                    _accepting = false;
                    _ready.Writer.TryComplete();
                    break;
                }
            }

            await Task.WhenAll(_workers);
        }

        public async ValueTask DisposeAsync() => await CompleteAsync();

        private static TaskCompletionSource CompletedSource()
        {
            TaskCompletionSource source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            source.SetResult();
            return source;
        }
    }
}
