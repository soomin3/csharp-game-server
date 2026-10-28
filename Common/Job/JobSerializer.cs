using System.Threading.Channels;

namespace Common.Job
{
    public class JobSerializer
    {
        private readonly Channel<Action> _jobChannel = Channel.CreateUnbounded<Action>();
        private readonly object _executionGate = new object();
        private readonly RoomScheduler? _scheduler;
        private int _scheduled;

        public JobSerializer(RoomScheduler? scheduler = null)
        {
            _scheduler = scheduler;
        }

        public void Push(Action job)
        {
            ArgumentNullException.ThrowIfNull(job);
            _scheduler?.JobQueued();
            _jobChannel.Writer.TryWrite(job);
            TrySchedule();
        }

        public void Flush()
        {
            lock (_executionGate)
            {
                while (_jobChannel.Reader.TryRead(out Action? job))
                {
                    Execute(job);
                }
            }
        }

        public Task BarrierAsync()
        {
            TaskCompletionSource source = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Push(() => source.TrySetResult());
            return source.Task;
        }

        public async Task RunAsync(CancellationToken cancellationToken = default)
        {
            if (_scheduler != null)
            {
                throw new InvalidOperationException("스케줄러를 쓰는 방은 별도 RunAsync를 실행하지 않습니다.");
            }

            while (await _jobChannel.Reader.WaitToReadAsync(cancellationToken))
            {
                Flush();
            }
        }

        internal void RunScheduledBatch()
        {
            lock (_executionGate)
            {
                for (int count = 0; count < _scheduler!.BatchSize && _jobChannel.Reader.TryRead(out Action? job); count++)
                {
                    Execute(job);
                }

                if (_jobChannel.Reader.TryPeek(out _))
                {
                    _scheduler.Schedule(this);
                    return;
                }

                Volatile.Write(ref _scheduled, 0);
                // 빈 줄 확인 직후 생산자가 넣었어도 반드시 다음 실행을 예약한다.
                TrySchedule();
            }
        }

        private void TrySchedule()
        {
            if (_scheduler != null && _jobChannel.Reader.TryPeek(out _) &&
                Interlocked.CompareExchange(ref _scheduled, 1, 0) == 0)
            {
                _scheduler.Schedule(this);
            }
        }

        private void Execute(Action job)
        {
            try
            {
                job();
            }
            catch (Exception e)
            {
                Console.WriteLine($"[JobSerializer] 일감 실행 중 예외: {e}");
            }
            finally
            {
                _scheduler?.JobCompleted();
            }
        }
    }
}
