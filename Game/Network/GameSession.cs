using System.Net.Sockets;
using Common.Network;

namespace Game.Network
{
    public enum GameSessionState
    {
        Connected,
        LoggedIn,
        Disconnected,
    }

    public interface IGameSession
    {
        string Name { get; set; }
        GameSessionState State { get; set; }
        bool IsDisconnected { get; }
        void SendPacket(byte[] packet);
        void Disconnect(string? reason = null);
    }

    public class GameSession : TSession, IGameSession
    {
        private readonly GameHandler _handler;
        private readonly PacketDispatcher _dispatcher;

        public string Name { get; set; } = "(로그인 전)";
        public GameSessionState State { get; set; } = GameSessionState.Connected;

        public GameSession(TcpClient client, GameHandler handler) : base(client)
        {
            _handler = handler;
            _dispatcher = new PacketDispatcher(handler);
        }

        protected override void OnSessionConnected()
        {
            Console.WriteLine("[GameSession] 접속됨");
        }

        protected override void OnSessionDisconnected()
        {
            Console.WriteLine("[GameSession] 접속 끊김");
            _handler.OnDisconnect(this);
        }

        protected override void OnPacket(PacketId id, byte[] buffer, int offset, int length)
        {
            _dispatcher.Dispatch(this, id, buffer, offset, length);
        }
    }
}
