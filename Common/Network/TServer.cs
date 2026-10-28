using System.Net;
using System.Net.Sockets;

namespace Common.Network
{
    public class TServer
    {
        private readonly TcpListener _listener;
        private readonly int _port;
        private readonly Func<TcpClient, TSession> _sessionFactory;

        public TServer(int port, Func<TcpClient, TSession> sessionFactory)
        {
            _port = port;
            _listener = new TcpListener(IPAddress.Any, port);
            _sessionFactory = sessionFactory;
        }

        public async Task RunAsync()
        {
            _listener.Start();
            Console.WriteLine($"[TServer] {_port} 포트에서 대기 시작");

            while (true)
            {
                TcpClient client = await _listener.AcceptTcpClientAsync();
                TSession session = _sessionFactory(client);
                _ = session.RunAsync();
            }
        }
    }
}