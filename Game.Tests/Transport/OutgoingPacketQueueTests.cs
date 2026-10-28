using Common.Network;

namespace Queue.Tests
{
    public class OutgoingPacketQueueTests
    {
        [Fact]
        public void 소비자가_멈춰도_패킷_한도에서_즉시_실패한다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue(new SessionOptions
            {
                MaxPendingPackets = 3,
                MaxPendingBytes = 100,
            });

            Assert.True(queue.TryWrite(new byte[8]));
            Assert.True(queue.TryWrite(new byte[8]));
            Assert.True(queue.TryWrite(new byte[8]));
            Assert.False(queue.TryWrite(new byte[8]));
            Assert.Equal(3, queue.PendingPackets);
            Assert.Equal(24, queue.PendingBytes);
            queue.Abort();
            AssertEmpty(queue);
        }

        [Fact]
        public async Task 진행_중인_쓰기의_바이트도_한도에_포함한다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue(new SessionOptions
            {
                MaxPendingPackets = 4,
                MaxPendingBytes = 10,
            });
            Assert.True(queue.TryWrite(new byte[6]));
            using OutgoingPacket writing = await queue.ReadAsync();

            Assert.Equal(6, queue.PendingBytes);
            Assert.False(queue.TryWrite(new byte[5]));
            Assert.True(queue.TryWrite(new byte[4]));
            Assert.Equal(10, queue.PendingBytes);

            writing.Dispose();

            Assert.Equal(4, queue.PendingBytes);
            Assert.True(queue.TryWrite(new byte[6]));
            queue.Abort();
            AssertEmpty(queue);
        }

        [Fact]
        public async Task 진행_중인_쓰기의_패킷_수도_한도에_포함한다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue(new SessionOptions
            {
                MaxPendingPackets = 1,
                MaxPendingBytes = 100,
            });
            Assert.True(queue.TryWrite(new byte[1]));
            using OutgoingPacket writing = await queue.ReadAsync();

            Assert.False(queue.TryWrite(new byte[1]));
            writing.Dispose();
            Assert.True(queue.TryWrite(new byte[1]));
        }

        [Fact]
        public void 동시에_넣어도_패킷과_바이트_한도를_넘지_않는다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue(new SessionOptions
            {
                MaxPendingPackets = 32,
                MaxPendingBytes = 96,
            });
            int accepted = 0;

            Parallel.For(0, 512, _ =>
            {
                if (queue.TryWrite(new byte[3]))
                {
                    Interlocked.Increment(ref accepted);
                }
            });

            Assert.Equal(32, accepted);
            Assert.Equal(32, queue.PendingPackets);
            Assert.Equal(96, queue.PendingBytes);
            queue.Abort();
            AssertEmpty(queue);
        }

        [Fact]
        public async Task 여러_생산자와_소비자가_끝나면_받은_수와_바이트가_맞고_대기는_0이다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue(new SessionOptions
            {
                MaxPendingPackets = 64,
                MaxPendingBytes = 256,
            });
            int acceptedPackets = 0;
            long acceptedBytes = 0;
            int processedPackets = 0;
            long processedBytes = 0;
            Task consumer = Task.Run(async () =>
            {
                await foreach (OutgoingPacket packet in queue.ReadAllAsync())
                {
                    using (packet)
                    {
                        processedPackets++;
                        processedBytes += packet.Data.Length;
                        await Task.Yield();
                    }
                }
            });

            await Task.Run(() => Parallel.For(0, 8, producer =>
            {
                for (int i = 0; i < 1000; i++)
                {
                    byte[] packet = new byte[(producer + i) % 17 + 1];
                    if (queue.TryWrite(packet))
                    {
                        Interlocked.Increment(ref acceptedPackets);
                        Interlocked.Add(ref acceptedBytes, packet.Length);
                    }
                    Assert.InRange(queue.PendingPackets, 0, 64);
                    Assert.InRange(queue.PendingBytes, 0, 256);
                }
            }));
            queue.Complete();
            await consumer.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.True(acceptedPackets > 0);
            Assert.Equal(acceptedPackets, processedPackets);
            Assert.Equal(acceptedBytes, processedBytes);
            AssertEmpty(queue);
        }

        [Fact]
        public async Task 정상_완료는_새_입력을_막고_기존_항목을_모두_처리한다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue();
            Assert.True(queue.TryWrite(new byte[3]));
            Assert.True(queue.TryWrite(new byte[7]));
            queue.Complete();
            queue.Complete();

            Assert.False(queue.TryWrite(new byte[1]));
            using MemoryStream stream = new MemoryStream();
            await WriteAllAsync(queue, stream);

            Assert.Equal(10, stream.Length);
            AssertEmpty(queue);
        }

        [Fact]
        public async Task 쓰기_실패_후_종료하면_진행_항목과_대기_항목을_모두_해제한다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue();
            Assert.True(queue.TryWrite(new byte[3]));
            Assert.True(queue.TryWrite(new byte[7]));
            queue.Complete();
            using FailingStream stream = new FailingStream();

            await Assert.ThrowsAsync<IOException>(() => WriteAllAsync(queue, stream));
            Assert.Equal(1, queue.PendingPackets);
            Assert.Equal(7, queue.PendingBytes);

            queue.Abort();

            Assert.False(queue.TryWrite(new byte[1]));
            AssertEmpty(queue);
        }

        [Fact]
        public async Task 강제_종료는_대기만_버리고_진행_중인_항목은_쓰기_끝에_해제한다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue();
            Assert.True(queue.TryWrite(new byte[3]));
            Assert.True(queue.TryWrite(new byte[7]));
            using OutgoingPacket writing = await queue.ReadAsync();

            queue.Abort();
            queue.Abort();
            queue.Complete();

            Assert.Equal(1, queue.PendingPackets);
            Assert.Equal(3, queue.PendingBytes);
            Assert.False(queue.TryWrite(new byte[1]));

            writing.Dispose();

            AssertEmpty(queue);
        }

        [Fact]
        public async Task 한_항목을_동시에_여러_번_해제해도_한_번만_차감한다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue();
            Assert.True(queue.TryWrite(new byte[16]));
            OutgoingPacket writing = await queue.ReadAsync();

            Parallel.For(0, 100, _ => writing.Dispose());

            AssertEmpty(queue);
            Assert.Throws<ObjectDisposedException>(() => writing.Data);
        }

        [Fact]
        public async Task 읽기_대기를_취소해도_한도를_쓰거나_큐를_닫지_않는다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue();
            using CancellationTokenSource cancellation = new CancellationTokenSource();
            Task<OutgoingPacket> read = queue.ReadAsync(cancellation.Token).AsTask();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);

            AssertEmpty(queue);
            Assert.True(queue.TryWrite(new byte[1]));
        }

        [Fact]
        public void 기본_한도는_500명_입장_알림_묶음을_받는다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue();

            for (int i = 0; i < 500; i++)
            {
                Assert.True(queue.TryWrite(new byte[100]));
            }

            Assert.Equal(500, queue.PendingPackets);
            Assert.Equal(50000, queue.PendingBytes);
            queue.Abort();
            AssertEmpty(queue);
        }

        [Fact]
        public void 바이트_한도보다_큰_하나의_패킷은_아무_한도도_차지하지_않는다()
        {
            using OutgoingPacketQueue queue = new OutgoingPacketQueue(new SessionOptions { MaxPendingBytes = 10 });

            Assert.False(queue.TryWrite(new byte[11]));

            AssertEmpty(queue);
        }

        private static void AssertEmpty(OutgoingPacketQueue queue)
        {
            Assert.Equal(0, queue.PendingPackets);
            Assert.Equal(0, queue.PendingBytes);
        }

        // TSession 연동 시에도 쓰기 성공/실패와 무관하게 using으로 한도를 돌려준다.
        private static async Task WriteAllAsync(OutgoingPacketQueue queue, Stream stream)
        {
            await foreach (OutgoingPacket packet in queue.ReadAllAsync())
            {
                using (packet)
                {
                    await stream.WriteAsync(packet.Data);
                }
            }
        }

        private sealed class FailingStream : MemoryStream
        {
            public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
                ValueTask.FromException(new IOException("검증용 쓰기 실패"));
        }
    }
}
