using System.Threading.Channels;

namespace Common.Job
{
    public class JobSerializer
    {
        private readonly Channel<Action> _jobChannel = Channel.CreateUnbounded<Action>();

        public void Push(Action job)
        {
            _jobChannel.Writer.TryWrite(job);
        }

        public void Flush()
        {
            while (_jobChannel.Reader.TryRead(out Action? job))
            {
                try
                {
                    job();
                }
                catch (Exception e)
                {
                    Console.WriteLine($"[JobSerializer] 일감 실행 중 예외: {e}");
                }
            }
        }

        public async Task RunAsync()
        {
            while (await _jobChannel.Reader.WaitToReadAsync())
            {
                Flush();
            }
        }
    }
}