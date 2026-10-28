using System.Net;
using System.Net.Sockets;
using System.Text;
using Common.Network;

namespace Queue.Tests;

[CollectionDefinition("Global console writer", DisableParallelization = true)]
public sealed class GlobalConsoleWriterCollection
{
}

[Collection("Global console writer")]
public sealed class LoggingFailureTransportTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task 로그_IO_실패가_있어도_실제_연결과_대기와_세션을_정리한다(bool disposedWriter, bool startSessionFirst)
    {
        using ConnectedSockets sockets = await ConnectedSockets.CreateAsync();
        ProbeSession session = new ProbeSession(sockets.Server);
        session.SendPacket(new byte[16]);
        Assert.Equal(1, session.PendingSendPackets);
        Task? running = null;
        if (startSessionFirst)
        {
            running = session.RunAsync();
            await session.Connected.Task.WaitAsync(Timeout);
        }

        TextWriter original = Console.Out;
        try
        {
            Console.SetOut(new ThrowingWriter(disposedWriter));

            session.Disconnect("검증용 로그 실패");
            running ??= session.RunAsync();
            await running.WaitAsync(Timeout);
        }
        finally
        {
            Console.SetOut(original);
            session.Disconnect();
        }

        Assert.True(session.IsDisconnected);
        Assert.Equal(0, session.PendingSendPackets);
        Assert.Equal(0, session.PendingSendBytes);
        Assert.Equal(1, session.DisconnectedCount);
        await AssertPeerClosedAsync(sockets.Client);
    }

    private static async Task AssertPeerClosedAsync(TcpClient client)
    {
        using CancellationTokenSource cancellation = new CancellationTokenSource(Timeout);
        byte[] buffer = new byte[128];
        try
        {
            while (await client.GetStream().ReadAsync(buffer, cancellation.Token) != 0)
            {
                // 종료 전에 이미 OS에 전달된 패킷이 있으면 먼저 읽는다.
            }
        }
        catch (IOException)
        {
            // EOF 대신 TCP reset으로 종료가 보고될 수도 있다.
        }
    }

    private sealed class ThrowingWriter(bool disposed) : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Fail();
        public override void Write(string? value) => Fail();
        public override void WriteLine(string? value) => Fail();

        private void Fail()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ThrowingWriter));
            }
            throw new IOException("검증용 콘솔 쓰기 실패");
        }
    }

    private sealed class ProbeSession(TcpClient client) : TSession(client)
    {
        private int _disconnectedCount;
        public int DisconnectedCount => Volatile.Read(ref _disconnectedCount);
        public TaskCompletionSource Connected { get; } = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override void OnSessionConnected() => Connected.TrySetResult();
        protected override void OnSessionDisconnected() => Interlocked.Increment(ref _disconnectedCount);
        protected override void OnPacket(PacketId id, byte[] buffer, int offset, int length) { }
    }

    private sealed class ConnectedSockets(TcpClient client, TcpClient server) : IDisposable
    {
        public TcpClient Client { get; } = client;
        public TcpClient Server { get; } = server;

        public static async Task<ConnectedSockets> CreateAsync()
        {
            TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            TcpClient client = new TcpClient();
            try
            {
                Task<TcpClient> accepting = listener.AcceptTcpClientAsync();
                await client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
                return new ConnectedSockets(client, await accepting.WaitAsync(Timeout));
            }
            catch
            {
                client.Dispose();
                throw;
            }
            finally
            {
                listener.Stop();
            }
        }

        public void Dispose()
        {
            Client.Dispose();
            Server.Dispose();
        }
    }
}
